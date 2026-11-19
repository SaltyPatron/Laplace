#include "postgres.h"

#include <limits.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/highway_table.h"
#include "laplace/core/relation_law.h"

#include "consensus_scan.h"
#include "edges_raw_internal.h"
#include "perfcache_native.h"
#include "spi_common.h"

#define ANCESTRY_STATE_CAP 262144

typedef struct AncestryEdge
{
    hash128_t subject;
    hash128_t object;
    int64     eff_mu;
} AncestryEdge;

typedef struct AncestryPathNode
{
    hash128_t node;
    int32     parent_idx;
} AncestryPathNode;

typedef struct AncestryAgg
{
    hash128_t id;
    int32     min_depth;
    int64     max_eff_mu;
} AncestryAgg;

typedef struct AncestryEdgeBuf
{
    MemoryContext caller;
    AncestryEdge *items;
    int           count;
    int           cap;
} AncestryEdgeBuf;

typedef struct CoupleKey
{
    hash128_t root;
    hash128_t node;
    int32     side_idx;
} CoupleKey;

typedef struct CoupleReached
{
    CoupleKey key;
    int32     min_depth;
} CoupleReached;

typedef struct CoupleExpanded
{
    CoupleKey key;
} CoupleExpanded;

typedef struct CoupleFrontierItem
{
    hash128_t root;
    hash128_t node;
    int32     side_idx;
} CoupleFrontierItem;

static int
cmp_hash128_raw(const void *pa, const void *pb)
{
    return memcmp(pa, pb, sizeof(hash128_t));
}

static int
cmp_ancestry_edge(const void *pa, const void *pb)
{
    const AncestryEdge *a = (const AncestryEdge *) pa;
    const AncestryEdge *b = (const AncestryEdge *) pb;
    int                 c = memcmp(&a->subject, &b->subject, sizeof(hash128_t));

    if (c != 0)
        return c;
    c = memcmp(&a->object, &b->object, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (a->eff_mu != b->eff_mu)
        return a->eff_mu > b->eff_mu ? -1 : 1;
    return 0;
}

static int
cmp_ancestry_agg(const void *pa, const void *pb)
{
    const AncestryAgg *a = (const AncestryAgg *) pa;
    const AncestryAgg *b = (const AncestryAgg *) pb;

    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static void
ancestry_consume(const LaplaceConsensusRow *row, void *ctx)
{
    AncestryEdgeBuf *buf = (AncestryEdgeBuf *) ctx;
    __int128         mu;
    int64            eff;
    MemoryContext    prev;

    if (row->object_is_null)
        return;
    mu = (__int128) row->rating - 2 * (__int128) row->rd;
    if (mu <= 0)
        return;
    eff = mu > INT64_MAX ? INT64_MAX : (int64) mu;

    prev = MemoryContextSwitchTo(buf->caller);
    if (buf->count == buf->cap)
    {
        int new_cap = buf->cap == 0 ? 64 : buf->cap * 2;
        buf->items  = buf->items == NULL
            ? (AncestryEdge *) palloc((Size) new_cap * sizeof(AncestryEdge))
            : (AncestryEdge *) repalloc(buf->items, (Size) new_cap * sizeof(AncestryEdge));
        buf->cap    = new_cap;
    }
    buf->items[buf->count].subject = row->subject;
    buf->items[buf->count].object  = row->object;
    buf->items[buf->count].eff_mu  = eff;
    buf->count++;
    MemoryContextSwitchTo(prev);
}

static void
find_edge_span(const AncestryEdge *edges, int n_edges, const hash128_t *subj,
               int *out_start, int *out_end)
{
    int lo = 0;
    int hi = n_edges;

    while (lo < hi)
    {
        int mid = lo + (hi - lo) / 2;
        if (memcmp(&edges[mid].subject, subj, sizeof(hash128_t)) < 0)
            lo = mid + 1;
        else
            hi = mid;
    }
    *out_start = lo;
    while (lo < n_edges && memcmp(&edges[lo].subject, subj, sizeof(hash128_t)) == 0)
        lo++;
    *out_end = lo;
}

static void
find_raw_edge_span(const LaplaceEdgeRawRow *edges, int n_edges, const hash128_t *subj,
                   int *out_start, int *out_end)
{
    int lo = 0;
    int hi = n_edges;

    while (lo < hi)
    {
        int mid = lo + (hi - lo) / 2;
        if (memcmp(&edges[mid].subject_id, subj, sizeof(hash128_t)) < 0)
            lo = mid + 1;
        else
            hi = mid;
    }
    *out_start = lo;
    while (lo < n_edges && memcmp(&edges[lo].subject_id, subj, sizeof(hash128_t)) == 0)
        lo++;
    *out_end = lo;
}

PG_FUNCTION_INFO_V1(pg_laplace_taxonomy_ancestry);

Datum
pg_laplace_taxonomy_ancestry(PG_FUNCTION_ARGS)
{
    ReturnSetInfo   *rsinfo;
    hash128_t        root_id;
    bytea           *mask_ba;
    int32            max_depth;
    Size             mask_len;
    const uint8_t   *mask_bytes;
    hash128_t        type_ids[256];
    int              n_types = 0;
    ArrayType       *types_arr;
    HASHCTL          ctl;
    HTAB            *agg_tab;
    AncestryPathNode *history;
    int              hist_len = 0;
    int              hist_cap = 256;
    int             *cur_frontier;
    int              n_cur = 0;
    long             n_agg;

    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2))
        return (Datum) 0;

    max_depth = PG_GETARG_INT32(2);
    if (max_depth <= 0)
        return (Datum) 0;

    root_id    = datum_to_hash128(PG_GETARG_DATUM(0));
    mask_ba    = PG_GETARG_BYTEA_PP(1);
    mask_len   = VARSIZE_ANY_EXHDR(mask_ba);
    mask_bytes = (const uint8_t *) VARDATA_ANY(mask_ba);

    if (!laplace_highway_ready())
        ereport(ERROR, (errmsg("taxonomy.ancestry: highway perfcache is not loaded")));

    {
        int max_bits = mask_len > 32 ? 256 : (int) (mask_len * 8);
        for (int bit = 0; bit < max_bits; ++bit)
        {
            const char *canonical = NULL;
            float       rank;
            uint8_t     rec_band;
            hash128_t   tid;

            if (((mask_bytes[bit >> 3] >> (bit & 7)) & 1) == 0)
                continue;
            if (highway_table_relation_by_bit((uint8_t) bit, &canonical, &rank, &rec_band) != 0)
                continue;
            if (laplace_relation_type_id(canonical, &tid) < 0)
                continue;
            type_ids[n_types++] = tid;
        }
    }

    if (n_types == 0)
        return (Datum) 0;

    qsort(type_ids, (Size) n_types, sizeof(hash128_t), cmp_hash128_raw);
    {
        int w = 1;
        for (int i = 1; i < n_types; ++i)
        {
            if (memcmp(&type_ids[w - 1], &type_ids[i], sizeof(hash128_t)) != 0)
                type_ids[w++] = type_ids[i];
        }
        n_types = w;
    }

    types_arr = hash128_array_from_ids(type_ids, n_types);

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize   = sizeof(hash128_t);
    ctl.entrysize = sizeof(AncestryAgg);
    ctl.hcxt      = CurrentMemoryContext;
    agg_tab = hash_create("taxonomy_ancestry agg", 128, &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    history = (AncestryPathNode *) palloc((Size) hist_cap * sizeof(AncestryPathNode));
    history[0].node       = root_id;
    history[0].parent_idx = -1;
    hist_len              = 1;

    cur_frontier    = (int *) palloc(sizeof(int));
    cur_frontier[0] = 0;
    n_cur           = 1;

    for (int32 depth = 1; depth <= max_depth && n_cur > 0; ++depth)
    {
        hash128_t      *probe_ids;
        int             n_probe = 0;
        ArrayType      *probe_arr;
        AncestryEdgeBuf ebuf;
        int             n_edges = 0;
        int            *next_frontier = NULL;
        int             next_count = 0;
        int             next_cap = 0;
        HTAB           *next_dedup = NULL;

        CHECK_FOR_INTERRUPTS();

        probe_ids = (hash128_t *) palloc((Size) n_cur * sizeof(hash128_t));
        for (int i = 0; i < n_cur; ++i)
            probe_ids[i] = history[cur_frontier[i]].node;
        qsort(probe_ids, (Size) n_cur, sizeof(hash128_t), cmp_hash128_raw);
        n_probe = 1;
        for (int i = 1; i < n_cur; ++i)
        {
            if (memcmp(&probe_ids[n_probe - 1], &probe_ids[i], sizeof(hash128_t)) != 0)
                probe_ids[n_probe++] = probe_ids[i];
        }

        probe_arr   = hash128_array_from_ids(probe_ids, n_probe);
        pfree(probe_ids);

        ebuf.caller = CurrentMemoryContext;
        ebuf.items  = NULL;
        ebuf.count  = 0;
        ebuf.cap    = 0;

        laplace_consensus_candidates_stored(probe_arr, NULL, types_arr,
                                            ancestry_consume, &ebuf);
        pfree(probe_arr);

        if (ebuf.count == 0)
        {
            pfree(cur_frontier);
            n_cur = 0;
            break;
        }

        qsort(ebuf.items, (Size) ebuf.count, sizeof(AncestryEdge), cmp_ancestry_edge);
        n_edges = 1;
        for (int i = 1; i < ebuf.count; ++i)
        {
            if (memcmp(&ebuf.items[n_edges - 1].subject, &ebuf.items[i].subject, sizeof(hash128_t)) != 0 ||
                memcmp(&ebuf.items[n_edges - 1].object, &ebuf.items[i].object, sizeof(hash128_t)) != 0)
            {
                ebuf.items[n_edges++] = ebuf.items[i];
            }
        }

        if (depth < max_depth && n_cur > 1024)
        {
            HASHCTL dctl;
            memset(&dctl, 0, sizeof(dctl));
            dctl.keysize   = sizeof(hash128_t);
            dctl.entrysize = sizeof(hash128_t);
            dctl.hcxt      = CurrentMemoryContext;
            next_dedup = hash_create("ancestry frontier dedup", 1024, &dctl,
                                     HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        }

        for (int k = 0; k < n_cur; ++k)
        {
            int              hidx = cur_frontier[k];
            const hash128_t *u = &history[hidx].node;
            int              span_start = 0;
            int              span_end = 0;

            find_edge_span(ebuf.items, n_edges, u, &span_start, &span_end);
            for (int ei = span_start; ei < span_end; ++ei)
            {
                const hash128_t *v = &ebuf.items[ei].object;
                int              p = hidx;
                bool             visited = false;
                bool             found = false;
                AncestryAgg     *agg;

                while (p >= 0)
                {
                    if (memcmp(&history[p].node, v, sizeof(hash128_t)) == 0)
                    {
                        visited = true;
                        break;
                    }
                    p = history[p].parent_idx;
                }
                if (visited)
                    continue;

                agg = (AncestryAgg *) hash_search(agg_tab, v, HASH_ENTER, &found);
                if (!found)
                {
                    agg->min_depth  = depth;
                    agg->max_eff_mu = ebuf.items[ei].eff_mu;
                }
                else
                {
                    if (depth < agg->min_depth)
                        agg->min_depth = depth;
                    if (ebuf.items[ei].eff_mu > agg->max_eff_mu)
                        agg->max_eff_mu = ebuf.items[ei].eff_mu;
                }

                if (depth < max_depth && hist_len < ANCESTRY_STATE_CAP)
                {
                    if (next_dedup != NULL)
                    {
                        bool seen_node = false;
                        (void) hash_search(next_dedup, v, HASH_ENTER, &seen_node);
                        if (seen_node)
                            continue;
                    }
                    if (hist_len == hist_cap)
                    {
                        hist_cap *= 2;
                        history = (AncestryPathNode *) repalloc(
                            history, (Size) hist_cap * sizeof(AncestryPathNode));
                    }
                    {
                        int new_idx = hist_len++;
                        history[new_idx].node       = *v;
                        history[new_idx].parent_idx = hidx;

                        if (next_count == next_cap)
                        {
                            next_cap = next_cap == 0 ? 64 : next_cap * 2;
                            next_frontier = next_frontier == NULL
                                ? (int *) palloc((Size) next_cap * sizeof(int))
                                : (int *) repalloc(next_frontier, (Size) next_cap * sizeof(int));
                        }
                        next_frontier[next_count++] = new_idx;
                    }
                }
            }
        }

        if (next_dedup != NULL)
            hash_destroy(next_dedup);
        pfree(ebuf.items);
        pfree(cur_frontier);
        cur_frontier = next_frontier;
        n_cur        = next_count;
    }

    if (cur_frontier != NULL)
        pfree(cur_frontier);
    pfree(history);
    pfree(types_arr);

    n_agg = hash_get_num_entries(agg_tab);
    if (n_agg > 0)
    {
        AncestryAgg    *out;
        HASH_SEQ_STATUS seq;
        AncestryAgg    *entry;
        long            idx = 0;

        if (n_agg > INT_MAX || (Size) n_agg > MaxAllocSize / sizeof(AncestryAgg))
            ereport(ERROR, (errmsg("taxonomy.ancestry: result exceeds allocation limit")));

        out = (AncestryAgg *) palloc((Size) n_agg * sizeof(AncestryAgg));
        hash_seq_init(&seq, agg_tab);
        while ((entry = (AncestryAgg *) hash_seq_search(&seq)) != NULL)
            out[idx++] = *entry;

        qsort(out, (Size) idx, sizeof(AncestryAgg), cmp_ancestry_agg);

        for (long i = 0; i < idx; ++i)
        {
            Datum vals[3];
            bool  nulls[3] = { false, false, false };

            vals[0] = hash128_to_datum(&out[i].id);
            vals[1] = Int32GetDatum(out[i].min_depth);
            vals[2] = eff_mu_display_numeric(out[i].max_eff_mu, 0);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        }
        pfree(out);
    }

    hash_destroy(agg_tab);
    return (Datum) 0;
}

static int
cmp_couple_reached(const void *pa, const void *pb)
{
    const CoupleReached *a = (const CoupleReached *) pa;
    const CoupleReached *b = (const CoupleReached *) pb;
    int                  c;

    c = memcmp(&a->key.root, &b->key.root, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (a->key.side_idx != b->key.side_idx)
        return a->key.side_idx < b->key.side_idx ? -1 : 1;
    return memcmp(&a->key.node, &b->key.node, sizeof(hash128_t));
}

PG_FUNCTION_INFO_V1(pg_laplace_couple_upward_walk);

Datum
pg_laplace_couple_upward_walk(PG_FUNCTION_ARGS)
{
    ReturnSetInfo      *rsinfo;
    ArrayType          *roots_arr;
    ArrayType          *sides_arr;
    ArrayType          *starts_arr;
    ArrayType          *upward_types;
    int32               max_depth;
    bool                has_limit;
    int32               edge_limit = 0;
    Datum              *r_datums, *s_datums, *st_datums;
    bool               *r_nulls, *s_nulls, *st_nulls;
    int                 n_roots, n_sides, n_starts, n_seeds;
    char              **side_strs = NULL;
    int                 n_side_strs = 0;
    int                 side_cap = 0;
    HASHCTL             ctl;
    HTAB               *reached;
    HTAB               *expanded;
    CoupleFrontierItem *frontier = NULL;
    int                 n_frontier = 0;
    int                 frontier_cap = 0;
    long                n_reached;

    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2) || PG_ARGISNULL(4))
        return (Datum) 0;

    max_depth = PG_GETARG_INT32(4);
    if (max_depth <= 0)
        return (Datum) 0;

    has_limit = !PG_ARGISNULL(5);
    if (has_limit)
    {
        edge_limit = PG_GETARG_INT32(5);
        if (edge_limit <= 0)
            return (Datum) 0;
    }

    roots_arr    = PG_GETARG_ARRAYTYPE_P(0);
    sides_arr    = PG_GETARG_ARRAYTYPE_P(1);
    starts_arr   = PG_GETARG_ARRAYTYPE_P(2);
    upward_types = PG_ARGISNULL(3) ? NULL : PG_GETARG_ARRAYTYPE_P(3);

    deconstruct_array(roots_arr, BYTEAOID, -1, false, TYPALIGN_INT, &r_datums, &r_nulls, &n_roots);
    deconstruct_array(sides_arr, TEXTOID, -1, false, TYPALIGN_INT, &s_datums, &s_nulls, &n_sides);
    deconstruct_array(starts_arr, BYTEAOID, -1, false, TYPALIGN_INT, &st_datums, &st_nulls, &n_starts);

    n_seeds = Min(n_roots, Min(n_sides, n_starts));
    if (n_seeds <= 0)
    {
        pfree(r_datums); pfree(r_nulls);
        pfree(s_datums); pfree(s_nulls);
        pfree(st_datums); pfree(st_nulls);
        return (Datum) 0;
    }

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize   = sizeof(CoupleKey);
    ctl.entrysize = sizeof(CoupleReached);
    ctl.hcxt      = CurrentMemoryContext;
    reached = hash_create("couple_upward reached", 256, &ctl,
                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    ctl.entrysize = sizeof(CoupleExpanded);
    expanded = hash_create("couple_upward expanded", 256, &ctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = 0; i < n_seeds; ++i)
    {
        char     *side_cstr;
        int32     side_idx = -1;
        CoupleKey key;
        bool      seen = false;

        if (r_nulls[i] || s_nulls[i] || st_nulls[i])
            continue;

        side_cstr = text_to_cstring(DatumGetTextPP(s_datums[i]));
        for (int j = 0; j < n_side_strs; ++j)
        {
            if (strcmp(side_strs[j], side_cstr) == 0)
            {
                side_idx = j;
                break;
            }
        }
        if (side_idx < 0)
        {
            if (n_side_strs == side_cap)
            {
                side_cap  = side_cap == 0 ? 4 : side_cap * 2;
                side_strs = side_strs == NULL
                    ? (char **) palloc((Size) side_cap * sizeof(char *))
                    : (char **) repalloc(side_strs, (Size) side_cap * sizeof(char *));
            }
            side_idx            = n_side_strs++;
            side_strs[side_idx] = side_cstr;
        }
        else
        {
            pfree(side_cstr);
        }

        memset(&key, 0, sizeof(key));
        key.root     = datum_to_hash128(r_datums[i]);
        key.node     = datum_to_hash128(st_datums[i]);
        key.side_idx = side_idx;

        (void) hash_search(expanded, &key, HASH_ENTER, &seen);
        if (!seen)
        {
            if (n_frontier == frontier_cap)
            {
                frontier_cap = frontier_cap == 0 ? 32 : frontier_cap * 2;
                frontier     = frontier == NULL
                    ? (CoupleFrontierItem *) palloc((Size) frontier_cap * sizeof(CoupleFrontierItem))
                    : (CoupleFrontierItem *) repalloc(frontier, (Size) frontier_cap * sizeof(CoupleFrontierItem));
            }
            frontier[n_frontier].root     = key.root;
            frontier[n_frontier].node     = key.node;
            frontier[n_frontier].side_idx = key.side_idx;
            n_frontier++;
        }
    }

    pfree(r_datums); pfree(r_nulls);
    pfree(s_datums); pfree(s_nulls);
    pfree(st_datums); pfree(st_nulls);

    for (int32 depth = 1; depth <= max_depth && n_frontier > 0; ++depth)
    {
        hash128_t          *probe_ids;
        int                 n_probe;
        ArrayType          *probe_arr;
        LaplaceEdgeRawRow  *edges;
        int                 n_edges = 0;
        CoupleFrontierItem *next_frontier = NULL;
        int                 n_next = 0;
        int                 next_cap = 0;

        CHECK_FOR_INTERRUPTS();

        probe_ids = (hash128_t *) palloc((Size) n_frontier * sizeof(hash128_t));
        for (int i = 0; i < n_frontier; ++i)
            probe_ids[i] = frontier[i].node;
        qsort(probe_ids, (Size) n_frontier, sizeof(hash128_t), cmp_hash128_raw);
        n_probe = 1;
        for (int i = 1; i < n_frontier; ++i)
        {
            if (memcmp(&probe_ids[n_probe - 1], &probe_ids[i], sizeof(hash128_t)) != 0)
                probe_ids[n_probe++] = probe_ids[i];
        }

        probe_arr = hash128_array_from_ids(probe_ids, n_probe);
        pfree(probe_ids);

        edges = laplace_edges_raw_batch_c(probe_arr, "out", upward_types,
                                          has_limit, edge_limit, false,
                                          "eff_mu", &n_edges);
        pfree(probe_arr);

        if (edges == NULL || n_edges == 0)
        {
            pfree(frontier);
            frontier   = NULL;
            n_frontier = 0;
            break;
        }

        for (int i = 0; i < n_frontier; ++i)
        {
            const CoupleFrontierItem *item = &frontier[i];
            int                       span_start = 0;
            int                       span_end = 0;

            find_raw_edge_span(edges, n_edges, &item->node, &span_start, &span_end);
            for (int ei = span_start; ei < span_end; ++ei)
            {
                CoupleKey      nk;
                CoupleReached *r_entry;
                bool           r_found = false;

                if (edges[ei].neighbour_null)
                    continue;

                memset(&nk, 0, sizeof(nk));
                nk.root     = item->root;
                nk.node     = edges[ei].neighbour_id;
                nk.side_idx = item->side_idx;

                r_entry = (CoupleReached *) hash_search(reached, &nk, HASH_ENTER, &r_found);
                if (!r_found)
                    r_entry->min_depth = depth;

                if (depth < max_depth)
                {
                    bool e_found = false;
                    (void) hash_search(expanded, &nk, HASH_ENTER, &e_found);
                    if (!e_found)
                    {
                        if (n_next == next_cap)
                        {
                            next_cap = next_cap == 0 ? 64 : next_cap * 2;
                            next_frontier = next_frontier == NULL
                                ? (CoupleFrontierItem *) palloc((Size) next_cap * sizeof(CoupleFrontierItem))
                                : (CoupleFrontierItem *) repalloc(next_frontier, (Size) next_cap * sizeof(CoupleFrontierItem));
                        }
                        next_frontier[n_next].root     = nk.root;
                        next_frontier[n_next].node     = nk.node;
                        next_frontier[n_next].side_idx = nk.side_idx;
                        n_next++;
                    }
                }
            }
        }

        pfree(edges);
        pfree(frontier);
        frontier   = next_frontier;
        n_frontier = n_next;
    }

    if (frontier != NULL)
        pfree(frontier);

    n_reached = hash_get_num_entries(reached);
    if (n_reached > 0)
    {
        CoupleReached  *out;
        Datum          *side_datums;
        HASH_SEQ_STATUS seq;
        CoupleReached  *entry;
        long            idx = 0;

        if (n_reached > INT_MAX || (Size) n_reached > MaxAllocSize / sizeof(CoupleReached))
            ereport(ERROR, (errmsg("converse._couple_upward_walk: result exceeds allocation limit")));

        side_datums = (Datum *) palloc((Size) n_side_strs * sizeof(Datum));
        for (int i = 0; i < n_side_strs; ++i)
            side_datums[i] = CStringGetTextDatum(side_strs[i]);

        out = (CoupleReached *) palloc((Size) n_reached * sizeof(CoupleReached));
        hash_seq_init(&seq, reached);
        while ((entry = (CoupleReached *) hash_seq_search(&seq)) != NULL)
            out[idx++] = *entry;

        qsort(out, (Size) idx, sizeof(CoupleReached), cmp_couple_reached);

        for (long i = 0; i < idx; ++i)
        {
            Datum vals[4];
            bool  nulls[4] = { false, false, false, false };

            vals[0] = hash128_to_datum(&out[i].key.root);
            vals[1] = side_datums[out[i].key.side_idx];
            vals[2] = hash128_to_datum(&out[i].key.node);
            vals[3] = Int32GetDatum(out[i].min_depth);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
        }

        pfree(out);
        for (int i = 0; i < n_side_strs; ++i)
            pfree(DatumGetPointer(side_datums[i]));
        pfree(side_datums);
    }

    for (int i = 0; i < n_side_strs; ++i)
        pfree(side_strs[i]);
    if (side_strs != NULL)
        pfree(side_strs);

    hash_destroy(expanded);
    hash_destroy(reached);
    return (Datum) 0;
}
