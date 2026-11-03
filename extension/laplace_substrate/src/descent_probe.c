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
 * Shared presence core. Bits are set only for confirmed ids: first through the
 * tier-0 perfcache (a codepoint id is an entity id, so use_perfcache is valid
 * only for entities), then one set probe for the rest -- native HASH(id)
 * routing when identity_relation is given, else ordinals_sql.
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
 * Attestation presence. Each candidate routes by its subject to the one
 * HASH(subject_id) leaf that can hold it, and each touched leaf answers its
 * whole id set in one native primary-key array scan under the caller's
 * snapshot.
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

int
laplace_attestations_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count)
{
    return batch_presence_core(ids_array, bm, candidate_count,
                               "SELECT idx FROM laplace.attestations_present_ordinals($1)",
                               false, NULL);
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
    /* Presence over a HASH(id)-partitioned relation: route the whole set with
     * PostgreSQL's partition hash support, then one native array index scan
     * per touched leaf under the caller's MVCC snapshot. */
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

    /* A root that is not HASH(id) partitioned, not readable by the role, or
     * under RLS is answered by ordinals_sql. The partition descriptor is read
     * each call so routing follows the live layout. */
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
    /* Grants on the parent need not exist on the leaves: a leaf the role
     * cannot read directly, or under its own RLS, sends the batch through the
     * root query. */
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
    /* Perfcache off: this answers whether a committed row exists. Tier-0
     * codepoint rows are written through the path that consults this probe;
     * counting them present by resolution would drop them from the write. */
    return batch_presence_core(ids_array, bm, candidate_count,
                               laplace_sql_query_text("entities.present_ordinals_fallback"),
                               false, "entities");
}

int
laplace_entities_stored_bitmap_keyed(ArrayType *ids_array, ArrayType *tiers_array,
                                     uint8_t *bm, int candidate_count)
{
    (void) tiers_array;
    return laplace_entities_stored_bitmap(ids_array, bm, candidate_count);
}

int
laplace_tier_batch_existence_probe_keyed(ArrayType *ids_array, ArrayType *tiers_array,
                                         uint8_t *bm, int candidate_count)
{
    (void) tiers_array;
    return laplace_tier_batch_existence_probe(ids_array, bm, candidate_count);
}

int
laplace_physicalities_present_bitmap_keyed(ArrayType *ids_array, ArrayType *hilberts_array,
                                           uint8_t *bm, int candidate_count)
{
    (void) hilberts_array;
    return laplace_physicalities_present_bitmap(ids_array, bm, candidate_count);
}
