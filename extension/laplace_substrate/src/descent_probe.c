#include "descent_probe.h"
#include "identity_scan.h"
#include "hash_leaves.h"
#include "laplace/core/sql_catalog.h"

#include "access/table.h"
#include "catalog/namespace.h"
#include "executor/spi.h"
#include "catalog/pg_type.h"
#include "miscadmin.h"
#include "partitioning/partbounds.h"
#include "partitioning/partdesc.h"
#include "utils/builtins.h"
#include "utils/acl.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/partcache.h"
#include "utils/rls.h"

#include "perfcache_native.h"

/*
 * Native keyed-probe routing (2026-07-21). Attestations is
 * LIST(type_id) -> HASH(subject_id). The caller already computed every
 * attestation id FROM (subject, type, object, source, context), so it holds the
 * exact partition keys -- the probe is a routing problem, and routing belongs in
 * C, not in the SQL planner (binding law; GH #565).
 *
 * The failed SQL forms all tried to make the PLANNER reconstruct the routing:
 * a column-ref join hash-joined against an Append of all 1,304 leaves (a full
 * 8.79M-row table scan to match a batch); a runtime variable pruned LIST but not
 * the HASH sublevel; only a BOUND LITERAL prunes LIST at plan time. The literal
 * form also forced a plpgsql temp-table-loop rebuilt on every call
 * (CREATE TEMP TABLE + CREATE INDEX + ANALYZE per probe chunk), and marking that
 * STABLE even crashed with 0A000.
 *
 * This routes it directly: group the batch by type in C, then per type execute a
 * SESSION-CACHED prepared plan whose type is a hex literal in the query text.
 * Plan-time LIST pruning happens once per type and is cached across every chunk
 * and every apply in the backend; runtime HASH pruning picks the one leaf per
 * row. One index descent per id, no temp table, no re-plan, no volatility trap.
 */

/* Entities are LIST(tier), with tier 2 further HASH(id).  The keyed entity
 * probes used to hand their arrays to a PL/pgSQL function which created and
 * indexed a temp table on every call, then looped over distinct tiers.  Tier
 * cardinality is tiny and the native caller already owns the parallel tier
 * array, so cache one literal-tier plan per backend instead.  A literal tier
 * prunes the LIST parent at plan time; the id value runtime-prunes tier 2's
 * HASH child. */
typedef struct EntityTierPlan
{
    int32      tier;            /* fixed-width hash key; avoids struct padding */
    SPIPlanPtr plan;
} EntityTierPlan;

static HTAB *entity_tier_plans = NULL;

static SPIPlanPtr
entity_tier_probe_plan(int16 tier)
{
    EntityTierPlan *entry;
    bool            found;
    int32           key = (int32) tier;

    if (entity_tier_plans == NULL)
    {
        HASHCTL ctl;

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(int32);
        ctl.entrysize = sizeof(EntityTierPlan);
        ctl.hcxt = TopMemoryContext;
        entity_tier_plans = hash_create("entity tier probe plans", 8,
                                        &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    }

    entry = (EntityTierPlan *) hash_search(entity_tier_plans, &key,
                                            HASH_ENTER, &found);
    if (!found)
    {
        char       sql[384];
        Oid        argtypes[2] = { BYTEAARRAYOID, INT4ARRAYOID };
        SPIPlanPtr plan;

        snprintf(sql, sizeof(sql),
                 "SELECT u.ord FROM unnest($1::bytea[], $2::int[]) AS u(id, ord) "
                 "JOIN laplace.entities e "
                 "ON e.tier = %d::smallint AND e.id = u.id",
                 (int) tier);
        plan = SPI_prepare_cursor(sql, 2, argtypes, CURSOR_OPT_PARALLEL_OK);
        if (plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("entity_tier_probe_plan: SPI_prepare failed: %s",
                            SPI_result_code_string(SPI_result))));
        if (SPI_keepplan(plan) != 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("entity_tier_probe_plan: SPI_keepplan failed")));
        entry->plan = plan;
    }
    return entry->plan;
}

static int
cmp_probe_by_tier(const void *a, const void *b, void *arg)
{
    const Datum *tiers = (const Datum *) arg;
    int          ia = *(const int *) a;
    int          ib = *(const int *) b;
    int16        ta = DatumGetInt16(tiers[ia]);
    int16        tb = DatumGetInt16(tiers[ib]);

    return (ta > tb) - (ta < tb);
}

static inline void
bitmap_set(uint8_t *bm, int pos)
{
    bm[pos >> 3] |= (uint8_t)(1u << (pos & 7u));
}

static int
spi_mark_present_ordinals(const char *sql, int narg, Oid *argtypes, Datum *args,
                          uint8_t *bm, int candidate_count)
{
    int spi_rc = SPI_execute_with_args(sql, narg, argtypes, args, NULL, true, 0);

    if (spi_rc != SPI_OK_SELECT)
        return spi_rc;

    for (uint64 i = 0; i < SPI_processed; i++)
    {
        bool  isnull;
        Datum d = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 1, &isnull);

        if (!isnull)
        {
            int pos = DatumGetInt32(d);

            if (pos >= 0 && pos < candidate_count)
                bitmap_set(bm, pos);
        }
    }
    return SPI_OK_SELECT;
}

/*
 * Shared batch-presence core used by both laplace_entities_present_bitmap()
 * and laplace_tier_batch_existence_probe(). `bm` is assumed pre-zeroed by
 * the caller (palloc0'd result buffer) -- this function only ever SETS bits
 * for ids it can positively confirm present, via:
 *   1. a perfcache fast-path lookup (tier-0 codepoints resolve without a
 *      DB round-trip at all), applied uniformly to every candidate
 *      regardless of what tier it's actually at -- codepoint ids simply
 *      won't match for tier>0 candidates, so this is a pure accelerant,
 *      never a special case; then
 *   2. exactly one SPI batch query for everything the perfcache fast path
 *      didn't resolve.
 * No default-present assumption, no tree-walk, no short-circuiting based on
 * an unconfirmed guess -- a bit is 1 iff this function actually confirmed
 * that id has a committed row in the probed table.
 *
 * `ordinals_sql` selects which table's present-ordinals probe answers the
 * batch query. `use_perfcache` gates the tier-0 codepoint fast path: valid
 * only when probing `entities` (a codepoint id IS an entity id by axiom);
 * other tables' ids derive differently and must always hit the real query.
 */
static int identity_presence_core(ArrayType *ids_array, uint8_t *bm,
    int candidate_count, const char *relation_name, const char *ordinals_sql);

static int
batch_presence_core(ArrayType *ids_array, uint8_t *bm, int candidate_count,
                    const char *ordinals_sql, bool use_perfcache, const char *identity_relation)
{
    Datum      *elems;
    bool       *nulls;
    int         nelems;
    int        *remap;
    Datum      *probe_elems;
    int         probe_n = 0;
    int         i;
    Oid         argtypes[1];
    Datum       args[1];
    ArrayType  *probe_array;
    uint8_t    *sub_bm;
    int         spi_rc;

    if (candidate_count <= 0)
        return SPI_OK_SELECT;

    deconstruct_array(ids_array, BYTEAOID, -1, false, 'i', &elems, &nulls, &nelems);
    if (nelems != candidate_count)
    {
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("batch_presence_core: array length mismatch")));
    }

    remap = (int *) palloc(sizeof(int) * candidate_count);
    probe_elems = (Datum *) palloc(sizeof(Datum) * candidate_count);

    for (i = 0; i < candidate_count; i++)
    {
        bytea       *b;
        const uint8_t *id;

        if (nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            continue;
        id = (const uint8_t *) VARDATA_ANY(b);

        if (use_perfcache && laplace_perfcache_ready())
        {
            uint32_t cp;

            if (laplace_perfcache_codepoint_for_id(id, &cp))
            {
                bitmap_set(bm, i);
                continue;
            }
        }
        remap[probe_n] = i;
        probe_elems[probe_n++] = elems[i];
    }

    if (probe_n == 0)
    {
        pfree(remap);
        pfree(probe_elems);
        pfree(elems);
        pfree(nulls);
        return SPI_OK_SELECT;
    }

    probe_array = construct_array(probe_elems, probe_n, BYTEAOID, -1, false, 'i');
    sub_bm = (uint8_t *) palloc0((probe_n + 7) / 8);
    argtypes[0] = BYTEAARRAYOID;
    args[0] = PointerGetDatum(probe_array);

    spi_rc = identity_relation != NULL
        ? identity_presence_core(probe_array, sub_bm, probe_n,
                                 identity_relation, ordinals_sql)
        : spi_mark_present_ordinals(ordinals_sql, 1, argtypes, args, sub_bm, probe_n);

    if (spi_rc == SPI_OK_SELECT)
    {
        for (i = 0; i < probe_n; i++)
        {
            if ((sub_bm[i >> 3] & (1u << (i & 7u))) != 0)
                bitmap_set(bm, remap[i]);
        }
    }

    pfree(sub_bm);
    pfree(probe_array);
    pfree(remap);
    pfree(probe_elems);
    pfree(elems);
    pfree(nulls);
    return spi_rc;
}

int
laplace_entities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    return batch_presence_core(ids_array, bm, candidate_count,
                               laplace_sql_query_text("entities.present_ordinals_fallback"),
                               true, "entities");
}

int
laplace_tier_batch_existence_probe(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    return batch_presence_core(ids_array, bm, candidate_count,
                               laplace_sql_query_text("entities.present_ordinals_fallback"),
                               true, "entities");
}

typedef struct IdentityPresence
{
    unsigned char id[16];
    bool present;
} IdentityPresence;

static void
mark_identity_presence(TupleTableSlot *slot, AttrNumber id, void *opaque);

/*
 * Attestation presence. Attestations are HASH(subject_id); an attestation id is
 * the content hash of its five-tuple and unique. Each candidate routes by its
 * subject to the one leaf that can hold it, and each touched leaf answers its
 * whole id set in one native primary-key array scan. No SQL, no per-type plan.
 */
int
laplace_attestations_present_bitmap_keyed(ArrayType *ids_array, ArrayType *type_ids_array,
                                          ArrayType *subject_ids_array,
                                          uint8_t *bm, int candidate_count)
{
    const char *label = "attestations presence";
    Datum *ids, *subjects, *types;
    bool *id_nulls, *subject_nulls, *type_nulls;
    int n, ns, nt;

    if (candidate_count <= 0) return SPI_OK_SELECT;
    deconstruct_array(ids_array, BYTEAOID, -1, false, 'i', &ids, &id_nulls, &n);
    deconstruct_array(subject_ids_array, BYTEAOID, -1, false, 'i', &subjects, &subject_nulls, &ns);
    deconstruct_array(type_ids_array, BYTEAOID, -1, false, 'i', &types, &type_nulls, &nt);
    if (n != candidate_count || ns != candidate_count || nt != candidate_count)
        ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                        errmsg("%s: array length mismatch", label)));

    const LaplaceHashLeaves *leaves = laplace_hash_leaves("attestations", label);
    int *leaf = palloc(sizeof(int) * n);
    int *counts = palloc0(sizeof(int) * leaves->count);
    int *offsets = palloc0(sizeof(int) * (leaves->count + 1));
    int *fill = palloc0(sizeof(int) * leaves->count);
    Datum *routed = palloc(sizeof(Datum) * n);
    Datum *valid_subjects = palloc(sizeof(Datum) * n);
    int *valid_index = palloc(sizeof(int) * n);
    int valid = 0;
    HASHCTL ctl;
    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(IdentityPresence);
    ctl.hcxt = CurrentMemoryContext;
    HTAB *presence = hash_create("attestation presence", Max(n, 1), &ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = 0; i < n; i++)
    {
        leaf[i] = -1;
        if (id_nulls[i] || subject_nulls[i] ||
            VARSIZE_ANY_EXHDR(DatumGetByteaPP(ids[i])) != 16)
            continue;
        valid_subjects[valid] = subjects[i];
        valid_index[valid++] = i;
    }
    {
        int *routes = palloc(sizeof(int) * Max(valid, 1));
        laplace_hash_leaf_route(leaves, valid_subjects, valid, routes, label);
        for (int k = 0; k < valid; k++)
        {
            leaf[valid_index[k]] = routes[k];
            counts[routes[k]]++;
        }
        pfree(routes);
    }
    for (int p = 0; p < leaves->count; p++)
        offsets[p + 1] = offsets[p] + counts[p];
    for (int i = 0; i < n; i++)
    {
        bool found;
        if (leaf[i] < 0) continue;
        routed[offsets[leaf[i]] + fill[leaf[i]]++] = ids[i];
        IdentityPresence *entry = hash_search(presence,
            VARDATA_ANY(DatumGetByteaPP(ids[i])), HASH_ENTER, &found);
        if (!found) entry->present = false;
    }
    for (int p = 0; p < leaves->count; p++)
    {
        if (counts[p] == 0) continue;
        CHECK_FOR_INTERRUPTS();
        ArrayType *id_array = construct_array(routed + offsets[p], counts[p],
                                              BYTEAOID, -1, false, 'i');
        if (!laplace_identity_scan(leaves->leaf_oids[p], id_array,
                                   mark_identity_presence, presence))
            ereport(ERROR, (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                            errmsg("%s: attestation leaf has no id-leading primary key", label)));
        pfree(id_array);
    }
    for (int i = 0; i < n; i++)
    {
        if (leaf[i] < 0) continue;
        IdentityPresence *entry = hash_search(presence,
            VARDATA_ANY(DatumGetByteaPP(ids[i])), HASH_FIND, NULL);
        if (entry != NULL && entry->present) bitmap_set(bm, i);
    }
    hash_destroy(presence);
    return SPI_OK_SELECT;
}

static void
mark_identity_presence(TupleTableSlot *slot, AttrNumber id, void *opaque)
{
    bool isnull;
    Datum value = slot_getattr(slot, id, &isnull);
    if (!isnull)
    {
        bytea *bytes = DatumGetByteaPP(value);
        if (VARSIZE_ANY_EXHDR(bytes) == 16)
        {
            IdentityPresence *entry = hash_search(opaque, VARDATA_ANY(bytes), HASH_FIND, NULL);
            if (entry != NULL) entry->present = true;
        }
    }
}

static int
identity_presence_core(ArrayType *ids_array, uint8_t *bm, int candidate_count,
                       const char *relation_name, const char *ordinals_sql)
{
    /* One physical implementation for every HASH(id) presence operation.
     * Route the complete set with PostgreSQL's partition hash support, then
     * perform native array index scans under the caller's MVCC snapshot. */
    if (candidate_count <= 0) return SPI_OK_SELECT;
    Oid root_oid = get_relname_relid(relation_name, get_namespace_oid("laplace", false));
    Relation root = table_open(root_oid, AccessShareLock);
    PartitionKey key = RelationGetPartitionKey(root);
    PartitionDesc desc;
    Datum *ids, *routed_ids;
    HASHCTL presence_ctl;
    HTAB *presence;
    bool *nulls;
    int n, *owners, *counts, *offsets, *next;
    int rc = SPI_OK_SELECT;

    /* Older layouts retain the existing generic semantics. Read the live
     * descriptor each call: a cached partition count cannot own routing. */
    if ((pg_class_aclcheck(root_oid, GetUserId(), ACL_SELECT) != ACLCHECK_OK &&
         pg_attribute_aclcheck(root_oid, get_attnum(root_oid, "id"),
                               GetUserId(), ACL_SELECT) != ACLCHECK_OK) ||
        check_enable_rls(root_oid, InvalidOid, true) == RLS_ENABLED ||
        key == NULL || key->strategy != PARTITION_STRATEGY_HASH ||
        key->partnatts != 1 || key->partattrs[0] != get_attnum(root_oid, "id"))
    {
        table_close(root, AccessShareLock);
        return batch_presence_core(ids_array, bm, candidate_count,
            ordinals_sql, false, NULL);
    }
    desc = RelationGetPartitionDesc(root, false);
    /* Parent grants need not be repeated on children. Keep the parent query
     * for those roles, and for children whose own RLS would change its result. */
    for (int p = 0; p < desc->nparts; p++)
    {
        Oid leaf = desc->oids[p];
        if (!desc->is_leaf[p] ||
            (pg_class_aclcheck(leaf, GetUserId(), ACL_SELECT) != ACLCHECK_OK &&
             pg_attribute_aclcheck(leaf, get_attnum(leaf, "id"),
                                   GetUserId(), ACL_SELECT) != ACLCHECK_OK) ||
            check_enable_rls(leaf, InvalidOid, true) == RLS_ENABLED)
        {
            table_close(root, AccessShareLock);
            return batch_presence_core(ids_array, bm, candidate_count,
                ordinals_sql, false, NULL);
        }
    }
    deconstruct_array(ids_array, BYTEAOID, -1, false, 'i', &ids, &nulls, &n);
    if (n != candidate_count)
        ereport(ERROR, (errmsg("identity probe candidate count mismatch")));
    owners = palloc(sizeof(int) * n);
    counts = palloc0(sizeof(int) * desc->nparts);
    offsets = palloc0(sizeof(int) * (desc->nparts + 1));
    next = palloc0(sizeof(int) * desc->nparts);
    routed_ids = palloc(sizeof(Datum) * n);
    memset(&presence_ctl, 0, sizeof(presence_ctl));
    presence_ctl.keysize = 16;
    presence_ctl.entrysize = sizeof(IdentityPresence);
    presence_ctl.hcxt = CurrentMemoryContext;
    presence = hash_create("identity batch presence", Max(n, 1), &presence_ctl,
        HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int i = 0; i < n; i++)
    {
        bool isnull[1] = {false};
        uint64 hash;
        int owner;
        owners[i] = -1;
        if (nulls[i] || VARSIZE_ANY_EXHDR(DatumGetByteaPP(ids[i])) != 16)
            continue;
        if (desc->boundinfo->nindexes == 0) continue;
        hash = compute_partition_hash_value(1, key->partsupfunc,
            key->partcollation, &ids[i], isnull);
        owner = desc->boundinfo->indexes[hash % desc->boundinfo->nindexes];
        if (owner < 0) continue;
        if (!desc->is_leaf[owner])
            ereport(ERROR, (errmsg("identity HASH child must be a leaf")));
        owners[i] = owner;
        counts[owner]++;
    }
    for (int p = 0; p < desc->nparts; p++)
        offsets[p + 1] = offsets[p] + counts[p];
    for (int i = 0; i < n; i++)
    {
        int owner = owners[i];
        if (owner < 0) continue;
        int at = offsets[owner] + next[owner]++;
        routed_ids[at] = ids[i];
        bool found;
        IdentityPresence *entry = hash_search(presence,
            VARDATA_ANY(DatumGetByteaPP(ids[i])), HASH_ENTER, &found);
        if (!found) entry->present = false;
    }
    for (int p = 0; p < desc->nparts; p++)
    {
        if (counts[p] == 0) continue;
        CHECK_FOR_INTERRUPTS();
        ArrayType *id_array = construct_array(routed_ids + offsets[p], counts[p],
            BYTEAOID, -1, false, 'i');
        if (!laplace_identity_scan(desc->oids[p], id_array, mark_identity_presence, presence))
        {
            rc = batch_presence_core(ids_array, bm, candidate_count,
                ordinals_sql, false, NULL);
            pfree(id_array);
            break;
        }
        pfree(id_array);
    }
    for (int i = 0; i < n; i++)
    {
        if (owners[i] < 0) continue;
        IdentityPresence *entry = hash_search(presence,
            VARDATA_ANY(DatumGetByteaPP(ids[i])), HASH_FIND, NULL);
        if (entry != NULL && entry->present) bitmap_set(bm, i);
    }
    hash_destroy(presence);
    pfree(ids);
    pfree(nulls);
    pfree(owners);
    pfree(counts);
    pfree(offsets);
    pfree(next);
    pfree(routed_ids);
    table_close(root, AccessShareLock);
    return rc;
}

int
laplace_physicalities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    return batch_presence_core(ids_array, bm, candidate_count,
        laplace_sql_query_text("physicalities.present_ordinals"), false, "physicalities");
}

int
laplace_entities_stored_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    /* Perfcache fast path deliberately OFF: this probe answers "is there a
     * committed entities ROW", not "is this id resolvable". The write lane's
     * in-transaction verification is what makes tier-0 codepoint rows stored
     * in the first place (the unicode seed) -- answering their presence
     * axiomatically here would subtract them from the write list and the
     * rows would never land. */
    return batch_presence_core(ids_array, bm, candidate_count,
                               laplace_sql_query_text("entities.present_ordinals_fallback"),
                               false, "entities");
}

/*
 * Tier-keyed batch presence: ids plus a parallel int2[] of tiers. The
 * per-tier ordinals overload prunes LIST(tier) at plan time; entities' t2
 * HASH(id) leaves prune per row via the id equality. Joint remap keeps the
 * three-way alignment when a malformed id is skipped. `use_perfcache`
 * retains the tier-0 codepoint fast path where the caller's semantics are
 * resolvability (descent), and stays off for stored-row semantics.
 */
static int
batch_presence_core_tiered(ArrayType *ids_array, ArrayType *tiers_array,
                           uint8_t *bm, int candidate_count,
                           bool use_perfcache)
{
    Datum      *elems, *t_elems;
    bool       *nulls, *t_nulls;
    int         nelems, t_n;
    int        *remap;
    Datum      *probe_elems, *probe_tiers;
    int        *order;
    int         probe_n = 0;
    int         i;
    int         spi_rc = SPI_OK_SELECT;

    if (candidate_count <= 0)
        return SPI_OK_SELECT;

    deconstruct_array(ids_array, BYTEAOID, -1, false, 'i', &elems, &nulls, &nelems);
    deconstruct_array(tiers_array, INT2OID, 2, true, 's', &t_elems, &t_nulls, &t_n);
    if (nelems != candidate_count || t_n != candidate_count)
    {
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("batch_presence_core_tiered: array length mismatch")));
    }

    remap = (int *) palloc(sizeof(int) * candidate_count);
    probe_elems = (Datum *) palloc(sizeof(Datum) * candidate_count);
    probe_tiers = (Datum *) palloc(sizeof(Datum) * candidate_count);
    order = (int *) palloc(sizeof(int) * candidate_count);

    for (i = 0; i < candidate_count; i++)
    {
        bytea       *b;
        const uint8_t *id;

        if (nulls[i] || t_nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            continue;
        id = (const uint8_t *) VARDATA_ANY(b);

        if (use_perfcache && laplace_perfcache_ready())
        {
            uint32_t cp;

            if (laplace_perfcache_codepoint_for_id(id, &cp))
            {
                bitmap_set(bm, i);
                continue;
            }
        }
        remap[probe_n] = i;
        probe_elems[probe_n] = elems[i];
        probe_tiers[probe_n] = t_elems[i];
        order[probe_n] = probe_n;
        probe_n++;
    }

    if (probe_n == 0)
    {
        spi_rc = SPI_OK_SELECT;
        goto done;
    }

    /* Sort only integer positions, leaving the deconstructed Datums owned by
     * their input arrays.  O(N log N), then one plan execution per tier. */
    qsort_arg(order, probe_n, sizeof(int), cmp_probe_by_tier, probe_tiers);

    for (i = 0; i < probe_n; )
    {
        int16       tier = DatumGetInt16(probe_tiers[order[i]]);
        int         end = i + 1;
        int         run_n;
        Datum      *run_ids;
        Datum      *run_ords;
        ArrayType  *ids_run;
        ArrayType  *ords_run;
        Datum       vals[2];
        SPIPlanPtr  plan;
        int         j;

        while (end < probe_n && DatumGetInt16(probe_tiers[order[end]]) == tier)
            end++;
        run_n = end - i;
        run_ids = (Datum *) palloc(sizeof(Datum) * run_n);
        run_ords = (Datum *) palloc(sizeof(Datum) * run_n);
        for (j = 0; j < run_n; j++)
        {
            int probe_idx = order[i + j];
            run_ids[j] = probe_elems[probe_idx];
            run_ords[j] = Int32GetDatum(remap[probe_idx]);
        }
        ids_run = construct_array(run_ids, run_n, BYTEAOID, -1, false, 'i');
        ords_run = construct_array(run_ords, run_n, INT4OID, 4, true, 'i');
        vals[0] = PointerGetDatum(ids_run);
        vals[1] = PointerGetDatum(ords_run);
        plan = entity_tier_probe_plan(tier);

        spi_rc = SPI_execute_plan(plan, vals, NULL, true, 0);
        if (spi_rc != SPI_OK_SELECT)
        {
            pfree(ids_run);
            pfree(ords_run);
            pfree(run_ids);
            pfree(run_ords);
            break;
        }
        for (uint64 row = 0; row < SPI_processed; row++)
        {
            bool  isnull;
            Datum d = SPI_getbinval(SPI_tuptable->vals[row],
                                    SPI_tuptable->tupdesc, 1, &isnull);
            if (!isnull)
            {
                int pos = DatumGetInt32(d);
                if (pos >= 0 && pos < candidate_count)
                    bitmap_set(bm, pos);
            }
        }

        pfree(ids_run);
        pfree(ords_run);
        pfree(run_ids);
        pfree(run_ords);
        i = end;
    }

done:
    pfree(order);
    pfree(remap);
    pfree(probe_elems);
    pfree(probe_tiers);
    pfree(elems);
    pfree(nulls);
    pfree(t_elems);
    pfree(t_nulls);
    return spi_rc;
}

/*
 * Pair-keyed batch presence: ids plus one parallel bytea[] key column
 * (physicalities: hilbert_index, the RANGE partition key). No perfcache
 * (physicality ids are never codepoint ids).
 */
static int
batch_presence_core_pair(ArrayType *ids_array, ArrayType *keys_array,
                         uint8_t *bm, int candidate_count,
                         const char *ordinals_sql)
{
    Datum      *elems, *k_elems;
    bool       *nulls, *k_nulls;
    int         nelems, k_n;
    int        *remap;
    Datum      *probe_elems, *probe_keys;
    int         probe_n = 0;
    int         i;
    Oid         argtypes[2];
    Datum       args[2];
    ArrayType  *probe_array, *keys_sub;
    uint8_t    *sub_bm;
    int         spi_rc;

    if (candidate_count <= 0)
        return SPI_OK_SELECT;

    deconstruct_array(ids_array, BYTEAOID, -1, false, 'i', &elems, &nulls, &nelems);
    deconstruct_array(keys_array, BYTEAOID, -1, false, 'i', &k_elems, &k_nulls, &k_n);
    if (nelems != candidate_count || k_n != candidate_count)
    {
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("batch_presence_core_pair: array length mismatch")));
    }

    remap = (int *) palloc(sizeof(int) * candidate_count);
    probe_elems = (Datum *) palloc(sizeof(Datum) * candidate_count);
    probe_keys = (Datum *) palloc(sizeof(Datum) * candidate_count);

    for (i = 0; i < candidate_count; i++)
    {
        bytea *b;

        if (nulls[i] || k_nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            continue;
        remap[probe_n] = i;
        probe_elems[probe_n] = elems[i];
        probe_keys[probe_n] = k_elems[i];
        probe_n++;
    }

    if (probe_n == 0)
    {
        spi_rc = SPI_OK_SELECT;
        goto done;
    }

    probe_array = construct_array(probe_elems, probe_n, BYTEAOID, -1, false, 'i');
    keys_sub = construct_array(probe_keys, probe_n, BYTEAOID, -1, false, 'i');
    sub_bm = (uint8_t *) palloc0((probe_n + 7) / 8);
    argtypes[0] = BYTEAARRAYOID;
    argtypes[1] = BYTEAARRAYOID;
    args[0] = PointerGetDatum(probe_array);
    args[1] = PointerGetDatum(keys_sub);

    spi_rc = spi_mark_present_ordinals(
        ordinals_sql,
        2, argtypes, args, sub_bm, probe_n);

    if (spi_rc == SPI_OK_SELECT)
    {
        for (i = 0; i < probe_n; i++)
        {
            if ((sub_bm[i >> 3] & (1u << (i & 7u))) != 0)
                bitmap_set(bm, remap[i]);
        }
    }

    pfree(sub_bm);
    pfree(probe_array);
    pfree(keys_sub);

done:
    pfree(remap);
    pfree(probe_elems);
    pfree(probe_keys);
    pfree(elems);
    pfree(nulls);
    pfree(k_elems);
    pfree(k_nulls);
    return spi_rc;
}

int
laplace_entities_stored_bitmap_keyed(ArrayType *ids_array, ArrayType *tiers_array,
                                     uint8_t *bm, int candidate_count)
{
    /* Stored-row semantics: perfcache OFF (see 1-arg comment). */
    return batch_presence_core_tiered(ids_array, tiers_array, bm, candidate_count,
                                      false);
}

int
laplace_tier_batch_existence_probe_keyed(ArrayType *ids_array, ArrayType *tiers_array,
                                         uint8_t *bm, int candidate_count)
{
    /* Descent resolvability semantics: perfcache ON (identical to 1-arg). */
    return batch_presence_core_tiered(ids_array, tiers_array, bm, candidate_count,
                                      true);
}

int
laplace_physicalities_present_bitmap_keyed(ArrayType *ids_array, ArrayType *hilberts_array,
                                           uint8_t *bm, int candidate_count)
{
    return batch_presence_core_pair(ids_array, hilberts_array, bm, candidate_count,
                                    "SELECT idx FROM laplace.physicalities_present_ordinals($1, $2)");
}
