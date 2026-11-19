/*
 * A sparse star over consensus from the seed ids. Each hop issues one set read
 * of up to p_fanout neighbours per frontier id, optionally restricted to the
 * given relation types. A reached entity's relevance is the highest product of
 * edge strengths over the reads that reach it (seeds enter at 1.0, tier 2).
 * p_hops and p_fanout are how far and how wide the star runs; p_budget bounds
 * only the returned tier-2 entities, ranked by relevance and scaled by 1e6.
 */
#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "access/table.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/acl.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "utils/rls.h"

#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "consensus_scan.h"
#include "hash_leaves.h"
#include "identity_scan.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_foundry_crawl);
PG_FUNCTION_INFO_V1(pg_laplace_foundry_crawl_neighbors);

typedef struct CrawlEntry
{
    char    key[16];
    double  rel;
    int     depth;
    int16   tier;
} CrawlEntry;

typedef struct WordCand
{
    hash128_t id;
    double    rel;
} WordCand;

typedef struct CrawlRawEdge
{
    hash128_t frontier_id;
    hash128_t object_id;
    hash128_t type_id;
    int64     rating;
    int64     rd;
    int64     eff_mu;
} CrawlRawEdge;

typedef struct CrawlNeighborRow
{
    hash128_t frontier_id;
    hash128_t object_id;
    int16     tier;
    int64     rating;
    int64     rd;
    int32     rn;
} CrawlNeighborRow;

typedef struct CrawlCandCollect
{
    MemoryContext caller;
    CrawlRawEdge *items;
    int           count;
    int           cap;
} CrawlCandCollect;

typedef struct CrawlEntityTier
{
    hash128_t id;
    int16     tier;
} CrawlEntityTier;

typedef struct CrawlTierScanCtx
{
    MemoryContext caller;
    AttrNumber    tier_att;
    HTAB         *tier_map;
} CrawlTierScanCtx;

static int
crawl_hash128_cmp(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static int
crawl_raw_edge_cmp(const void *pa, const void *pb)
{
    const CrawlRawEdge *a = (const CrawlRawEdge *) pa;
    const CrawlRawEdge *b = (const CrawlRawEdge *) pb;
    int                 c;

    c = memcmp(&a->frontier_id, &b->frontier_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (a->eff_mu != b->eff_mu)
        return (a->eff_mu > b->eff_mu) ? -1 : 1;
    c = memcmp(&a->object_id, &b->object_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    return memcmp(&a->type_id, &b->type_id, sizeof(hash128_t));
}

static int
crawl_neighbor_out_cmp(const void *pa, const void *pb)
{
    const CrawlNeighborRow *a = (const CrawlNeighborRow *) pa;
    const CrawlNeighborRow *b = (const CrawlNeighborRow *) pb;
    int                     c;

    c = memcmp(&a->frontier_id, &b->frontier_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    return (a->rn < b->rn) ? -1 : (a->rn > b->rn ? 1 : 0);
}

static int
word_cmp_desc(const void *a, const void *b)
{
    const WordCand *wa = (const WordCand *) a;
    const WordCand *wb = (const WordCand *) b;

    if (wa->rel < wb->rel) return 1;
    if (wa->rel > wb->rel) return -1;
    return memcmp(&wa->id, &wb->id, sizeof(hash128_t));
}

static void *
allocate_items(uint64 count, Size item_size, const char *what)
{
    if (count == 0)
        count = 1;
    if (count > (uint64) (MaxAllocSize / item_size))
        ereport(ERROR,
                (errmsg("foundry_crawl: %s exceeds PostgreSQL allocation capacity", what),
                 errdetail("Requested %llu items of %zu bytes.",
                           (unsigned long long) count, item_size)));
    return palloc((Size) count * item_size);
}

static void
visit_crawl_consensus_edge(const LaplaceConsensusRow *crow, void *context)
{
    CrawlCandCollect *ctx = (CrawlCandCollect *) context;
    MemoryContext     prev;

    if (crow->object_is_null)
        return;

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count == ctx->cap)
    {
        int new_cap = ctx->cap == 0 ? 128 : ctx->cap * 2;
        if ((Size) new_cap > MaxAllocSize / sizeof(CrawlRawEdge))
            ereport(ERROR, (errmsg("foundry_crawl: candidate edge buffer exceeds allocation capacity")));
        ctx->items = ctx->items == NULL
            ? (CrawlRawEdge *) palloc((Size) new_cap * sizeof(CrawlRawEdge))
            : (CrawlRawEdge *) repalloc(ctx->items, (Size) new_cap * sizeof(CrawlRawEdge));
        ctx->cap = new_cap;
    }
    ctx->items[ctx->count++] = (CrawlRawEdge){
        .frontier_id = crow->subject,
        .object_id   = crow->object,
        .type_id     = crow->type,
        .rating      = crow->rating,
        .rd          = crow->rd,
        .eff_mu      = (int64) ((__int128) crow->rating - 2 * (__int128) crow->rd)
    };
    MemoryContextSwitchTo(prev);
}

static void
visit_crawl_entity_tier(TupleTableSlot *slot, AttrNumber id_att, void *opaque)
{
    CrawlTierScanCtx *ctx = (CrawlTierScanCtx *) opaque;
    bool              id_null = false;
    bool              tier_null = false;
    Datum             d_id = slot_getattr(slot, id_att, &id_null);
    Datum             d_tier = slot_getattr(slot, ctx->tier_att, &tier_null);
    hash128_t         id;
    CrawlEntityTier  *entry;
    bool              found = false;
    MemoryContext     prev;

    if (id_null || tier_null)
        return;
    id = datum_to_hash128(d_id);

    prev = MemoryContextSwitchTo(ctx->caller);
    entry = (CrawlEntityTier *) hash_search(ctx->tier_map, &id, HASH_ENTER, &found);
    if (!found)
        entry->tier = DatumGetInt16(d_tier);
    MemoryContextSwitchTo(prev);
}

static CrawlNeighborRow *
foundry_crawl_neighbors_c(const hash128_t *subjects, int n_subjects,
                          int32 limit, ArrayType *types_arr,
                          int *out_count)
{
    MemoryContext            caller = CurrentMemoryContext;
    hash128_t               *uniq_subj;
    int                      n_uniq_subj = 0;
    ArrayType               *subj_arr;
    CrawlCandCollect         collect;
    CrawlRawEdge            *ranked = NULL;
    int                      n_ranked = 0;
    hash128_t               *obj_ids = NULL;
    int                      n_objs = 0;
    const LaplaceHashLeaves *leaves;
    AttrNumber               tier_att;
    Datum                   *obj_datums;
    int                     *remainders;
    int                     *leaf_counts;
    int                     *leaf_offsets;
    int                     *leaf_next;
    Datum                   *routed_datums;
    HASHCTL                  hctl;
    HTAB                    *tier_map;
    CrawlNeighborRow        *result = NULL;
    int                      n_result = 0;

    *out_count = 0;
    if (subjects == NULL || n_subjects <= 0 || limit <= 0)
        return NULL;

    uniq_subj = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_subjects);
    memcpy(uniq_subj, subjects, sizeof(hash128_t) * (Size) n_subjects);
    if (n_subjects > 1)
        qsort(uniq_subj, (Size) n_subjects, sizeof(hash128_t), crawl_hash128_cmp);
    n_uniq_subj = 1;
    for (int i = 1; i < n_subjects; ++i)
    {
        if (memcmp(&uniq_subj[n_uniq_subj - 1], &uniq_subj[i], sizeof(hash128_t)) != 0)
            uniq_subj[n_uniq_subj++] = uniq_subj[i];
    }

    subj_arr = hash128_array_from_ids(uniq_subj, n_uniq_subj);
    pfree(uniq_subj);

    memset(&collect, 0, sizeof(collect));
    collect.caller = caller;
    laplace_consensus_candidates_stored(subj_arr, NULL, types_arr,
                                        visit_crawl_consensus_edge, &collect);
    pfree(subj_arr);

    if (collect.count == 0)
        return NULL;

    if (collect.count > 1)
        qsort(collect.items, (Size) collect.count, sizeof(CrawlRawEdge), crawl_raw_edge_cmp);

    ranked = (CrawlRawEdge *) palloc(sizeof(CrawlRawEdge) * (Size) collect.count);
    {
        int32 rn = 0;
        for (int i = 0; i < collect.count; ++i)
        {
            if (i == 0 || memcmp(&collect.items[i].frontier_id,
                                 &collect.items[i - 1].frontier_id,
                                 sizeof(hash128_t)) != 0)
                rn = 1;
            else
                rn++;
            if (rn <= limit)
                ranked[n_ranked++] = collect.items[i];
        }
    }
    pfree(collect.items);

    if (n_ranked == 0)
    {
        pfree(ranked);
        return NULL;
    }

    obj_ids = (hash128_t *) palloc(sizeof(hash128_t) * (Size) n_ranked);
    for (int i = 0; i < n_ranked; ++i)
        obj_ids[i] = ranked[i].object_id;
    if (n_ranked > 1)
        qsort(obj_ids, (Size) n_ranked, sizeof(hash128_t), crawl_hash128_cmp);
    n_objs = 1;
    for (int i = 1; i < n_ranked; ++i)
    {
        if (memcmp(&obj_ids[n_objs - 1], &obj_ids[i], sizeof(hash128_t)) != 0)
            obj_ids[n_objs++] = obj_ids[i];
    }

    leaves = laplace_hash_leaves("entities", "foundry_crawl");
    {
        AclResult acl = pg_class_aclcheck(leaves->parent_oid, GetUserId(), ACL_SELECT);
        if (acl != ACLCHECK_OK)
            aclcheck_error(acl, OBJECT_TABLE, "laplace.entities");
        if (check_enable_rls(leaves->parent_oid, InvalidOid, true) == RLS_ENABLED)
            ereport(ERROR, (errmsg("foundry_crawl: cannot bypass row security on laplace.entities")));
        tier_att = get_attnum(leaves->parent_oid, "tier");
        if (tier_att <= 0)
            ereport(ERROR, (errmsg("foundry_crawl: laplace.entities.tier column is missing")));
    }

    obj_datums    = (Datum *) palloc(sizeof(Datum) * (Size) n_objs);
    remainders    = (int *) palloc(sizeof(int) * (Size) n_objs);
    leaf_counts   = (int *) palloc0(sizeof(int) * (Size) leaves->count);
    leaf_offsets  = (int *) palloc0(sizeof(int) * (Size) (leaves->count + 1));
    leaf_next     = (int *) palloc0(sizeof(int) * (Size) leaves->count);
    routed_datums = (Datum *) palloc(sizeof(Datum) * (Size) n_objs);

    for (int i = 0; i < n_objs; ++i)
        obj_datums[i] = hash128_to_datum(&obj_ids[i]);
    laplace_hash_leaf_route(leaves, obj_datums, n_objs, remainders, "foundry_crawl");
    for (int i = 0; i < n_objs; ++i)
    {
        if (remainders[i] >= 0 && remainders[i] < leaves->count)
            leaf_counts[remainders[i]]++;
    }
    for (int p = 0; p < leaves->count; ++p)
        leaf_offsets[p + 1] = leaf_offsets[p] + leaf_counts[p];
    for (int i = 0; i < n_objs; ++i)
    {
        int r = remainders[i];
        if (r >= 0 && r < leaves->count)
            routed_datums[leaf_offsets[r] + leaf_next[r]++] = obj_datums[i];
    }

    memset(&hctl, 0, sizeof(hctl));
    hctl.keysize   = sizeof(hash128_t);
    hctl.entrysize = sizeof(CrawlEntityTier);
    hctl.hcxt      = caller;
    tier_map = hash_create("foundry_crawl tiers", Max(n_objs, 16), &hctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    {
        CrawlTierScanCtx sctx = { caller, tier_att, tier_map };
        for (int p = 0; p < leaves->count; ++p)
        {
            ArrayType *leaf_arr;
            if (leaf_counts[p] == 0)
                continue;
            CHECK_FOR_INTERRUPTS();
            leaf_arr = construct_array(routed_datums + leaf_offsets[p],
                                       leaf_counts[p], BYTEAOID, -1, false, TYPALIGN_INT);
            if (!laplace_identity_scan(leaves->leaf_oids[p], leaf_arr,
                                       visit_crawl_entity_tier, &sctx))
                ereport(ERROR, (errmsg("foundry_crawl: laplace.entities leaf scan failed")));
            pfree(leaf_arr);
        }
    }

    result = (CrawlNeighborRow *) palloc(sizeof(CrawlNeighborRow) * (Size) n_ranked);
    {
        int32 rn = 0;
        for (int i = 0; i < n_ranked; ++i)
        {
            CrawlEntityTier *te;
            if (i == 0 || memcmp(&ranked[i].frontier_id,
                                 &ranked[i - 1].frontier_id,
                                 sizeof(hash128_t)) != 0)
                rn = 1;
            else
                rn++;
            te = (CrawlEntityTier *) hash_search(tier_map, &ranked[i].object_id, HASH_FIND, NULL);
            if (te == NULL)
                continue;
            result[n_result++] = (CrawlNeighborRow){
                .frontier_id = ranked[i].frontier_id,
                .object_id   = ranked[i].object_id,
                .tier        = te->tier,
                .rating      = ranked[i].rating,
                .rd          = ranked[i].rd,
                .rn          = rn
            };
        }
    }

    if (n_result > 1)
        qsort(result, (Size) n_result, sizeof(CrawlNeighborRow), crawl_neighbor_out_cmp);

    hash_destroy(tier_map);
    for (int i = 0; i < n_objs; ++i)
        pfree(DatumGetPointer(obj_datums[i]));
    pfree(obj_datums);
    pfree(remainders);
    pfree(leaf_counts);
    pfree(leaf_offsets);
    pfree(leaf_next);
    pfree(routed_datums);
    pfree(obj_ids);
    pfree(ranked);

    *out_count = n_result;
    return result;
}

Datum
pg_laplace_foundry_crawl_neighbors(PG_FUNCTION_ARGS)
{
    ReturnSetInfo    *rsinfo;
    ArrayType        *subj_arr;
    int32             limit;
    ArrayType        *types_arr = NULL;
    Datum            *elems;
    bool             *nulls;
    int               raw_n = 0;
    hash128_t        *subjects;
    int               n_subj = 0;
    CrawlNeighborRow *rows;
    int               n_rows = 0;

    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        return (Datum) 0;
    limit = PG_GETARG_INT32(1);
    if (limit <= 0)
        return (Datum) 0;
    subj_arr = PG_GETARG_ARRAYTYPE_P(0);
    if (!PG_ARGISNULL(2))
        types_arr = PG_GETARG_ARRAYTYPE_P(2);

    deconstruct_array(subj_arr, BYTEAOID, -1, false, TYPALIGN_INT, &elems, &nulls, &raw_n);
    if (raw_n == 0)
    {
        pfree(elems);
        pfree(nulls);
        return (Datum) 0;
    }
    subjects = (hash128_t *) palloc(sizeof(hash128_t) * (Size) raw_n);
    for (int i = 0; i < raw_n; ++i)
    {
        if (!nulls[i])
            subjects[n_subj++] = datum_to_hash128(elems[i]);
    }
    pfree(elems);
    pfree(nulls);

    rows = foundry_crawl_neighbors_c(subjects, n_subj, limit, types_arr, &n_rows);
    pfree(subjects);

    for (int i = 0; i < n_rows; ++i)
    {
        const CrawlNeighborRow *r = &rows[i];
        Datum vals[5] = {
            hash128_to_datum(&r->frontier_id),
            hash128_to_datum(&r->object_id),
            Int16GetDatum(r->tier),
            Int64GetDatum(r->rating),
            Int64GetDatum(r->rd)
        };
        bool  rnulls[5] = { false, false, false, false, false };

        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, rnulls);
    }
    if (rows != NULL)
        pfree(rows);
    return (Datum) 0;
}

Datum
pg_laplace_foundry_crawl(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType     *seedarr;
    Datum         *seed_elems;
    bool          *seed_nulls;
    int            seed_n;
    int32          budget;
    int32          hops;
    int32          fanout;
    bool           has_filter = false;
    ArrayType     *type_arr = NULL;
    HTAB          *seen;
    HASHCTL        ctl;
    hash128_t     *frontier;
    int            n_front = 0;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("foundry_crawl: seed id array must not be NULL")));

    budget = PG_ARGISNULL(1) ? 32000 : PG_GETARG_INT32(1);
    hops   = PG_ARGISNULL(2) ? 3     : PG_GETARG_INT32(2);
    fanout = PG_ARGISNULL(3) ? 64    : PG_GETARG_INT32(3);
    if (budget < 0 || hops < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("foundry_crawl: budget, hops, and fanout must not be negative")));

    seedarr = PG_GETARG_ARRAYTYPE_P(0);
    if (ARR_NDIM(seedarr) > 1 || ARR_ELEMTYPE(seedarr) != BYTEAOID)
        ereport(ERROR, (errmsg("foundry_crawl: seeds must be a 1-D bytea[]")));
    deconstruct_array(seedarr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &seed_elems, &seed_nulls, &seed_n);

    if (!PG_ARGISNULL(4))
    {
        ArrayType *tarr = PG_GETARG_ARRAYTYPE_P(4);

        if (ARR_NDIM(tarr) > 1 || ARR_ELEMTYPE(tarr) != BYTEAOID)
            ereport(ERROR,
                    (errmsg("foundry_crawl: rel_types must be a 1-D bytea[]")));
        if (ArrayGetNItems(ARR_NDIM(tarr), ARR_DIMS(tarr)) > 0)
        {
            has_filter = true;
            type_arr = tarr;
        }
    }

    InitMaterializedSRF(fcinfo, 0);
    if (budget == 0 || seed_n == 0)
        return (Datum) 0;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(CrawlEntry);
    seen = hash_create("foundry_crawl seen", seed_n > 0 ? seed_n : 1,
                       &ctl, HASH_ELEM | HASH_BLOBS);
    frontier = (hash128_t *) allocate_items((uint64) seed_n,
                                             sizeof(hash128_t), "seed frontier");

    for (int s = 0; s < seed_n; s++)
    {
        hash128_t   id;
        CrawlEntry *entry;
        bool         found;

        if (seed_nulls[s])
            continue;
        id = datum_to_hash128(seed_elems[s]);
        entry = (CrawlEntry *) hash_search(seen, &id, HASH_ENTER, &found);
        if (found)
            continue;
        entry->rel = 1.0;
        entry->depth = 0;
        entry->tier = 2;
        frontier[n_front++] = id;
    }

    if (n_front > 0 && hops > 0 && fanout > 0)
    {
        for (int depth = 0; depth < hops && n_front > 0; depth++)
        {
            CrawlNeighborRow *step_rows;
            int               n_step = 0;
            hash128_t        *next_front;
            int               n_next = 0;

            step_rows = foundry_crawl_neighbors_c(
                frontier, n_front, fanout, has_filter ? type_arr : NULL, &n_step);
            next_front = (hash128_t *) allocate_items((uint64) n_step,
                                                      sizeof(hash128_t), "next frontier");

            for (int r = 0; r < n_step; r++)
            {
                const CrawlNeighborRow *sr = &step_rows[r];
                CrawlEntry             *parent;
                CrawlEntry             *child;
                bool                    parent_found;
                bool                    child_found;
                double                  child_rel;

                parent = (CrawlEntry *) hash_search(seen, &sr->frontier_id,
                                                    HASH_FIND, &parent_found);
                if (!parent_found)
                    continue;
                child_rel = parent->rel * laplace_edge_strength(sr->rating, sr->rd);
                if (!isfinite(child_rel))
                    ereport(ERROR,
                            (errmsg("foundry_crawl: relevance overflow while traversing frontier")));

                child = (CrawlEntry *) hash_search(seen, &sr->object_id,
                                                   HASH_ENTER, &child_found);
                if (!child_found)
                {
                    child->rel = child_rel;
                    child->depth = depth + 1;
                    child->tier = sr->tier;
                    if (child->depth < hops)
                        next_front[n_next++] = sr->object_id;
                }
                else if (child_rel > child->rel)
                    child->rel = child_rel;

                CHECK_FOR_INTERRUPTS();
            }

            if (step_rows != NULL)
                pfree(step_rows);
            pfree(frontier);
            frontier = next_front;
            n_front = n_next;
        }
    }

    {
        long            n_seen = hash_get_num_entries(seen);
        WordCand       *words;
        int             n_words = 0;
        HASH_SEQ_STATUS seq;
        CrawlEntry     *entry;
        int             emit_n;

        if (n_seen > INT_MAX)
            ereport(ERROR,
                    (errmsg("foundry_crawl: visited set exceeds addressable result capacity")));
        words = (WordCand *) allocate_items((uint64) n_seen,
                                             sizeof(WordCand), "word candidates");
        hash_seq_init(&seq, seen);
        while ((entry = (CrawlEntry *) hash_seq_search(&seq)) != NULL)
        {
            if (entry->tier != 2)
                continue;
            memcpy(&words[n_words].id, entry->key, 16);
            words[n_words].rel = entry->rel;
            n_words++;
        }
        if (n_words > 1)
            qsort(words, (size_t) n_words, sizeof(WordCand), word_cmp_desc);

        emit_n = n_words < budget ? n_words : budget;
        for (int i = 0; i < emit_n; i++)
        {
            Datum       values[2];
            bool        nulls[2] = { false, false };
            long double scaled = (long double) words[i].rel * 1000000.0L;

            if (!isfinite((double) scaled) ||
                scaled > (long double) PG_INT64_MAX ||
                scaled < (long double) PG_INT64_MIN)
                ereport(ERROR,
                        (errmsg("foundry_crawl: result weight exceeds bigint capacity")));
            values[0] = hash128_to_datum(&words[i].id);
            values[1] = Int64GetDatum((int64) scaled);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
    }

    return (Datum) 0;
}
