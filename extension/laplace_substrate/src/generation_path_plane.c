#include "postgres.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "nodes/params.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/jsonb.h"
#include "utils/memutils.h"

#include "laplace/core/glicko2.h"
#include "laplace/core/hash128.h"
#include "laplace/core/sql_catalog.h"
#include "laplace_pg/executor.h"

#include "public_direct.h"
#include "relations_internal.h"
#include "spi_common.h"

typedef struct PathStepSpec
{
    ArrayType *types_arr;
    int        n_types;
    bool       reverse;
    bool       optional;
} PathStepSpec;

typedef struct PathClaim
{
    hash128_t claim_id;
    hash128_t subject_id;
    hash128_t type_id;
    hash128_t object_id;
    double    rating;
    double    rd;
} PathClaim;

typedef struct PathEdge
{
    hash128_t src_id;
    hash128_t dst_id;
    double    weight;
    bool      has_claim;
    PathClaim claim;
} PathEdge;

typedef struct PathStepNode
{
    hash128_t cur;
    hash128_t root;
    double    weight;
    int32     parent_idx;
    bool      has_claim;
    PathClaim claim;
} PathStepNode;

typedef struct PathAggregate
{
    hash128_t root;
    hash128_t target;
    double    total_weight;
    int      *path_indices;
    int       n_paths;
    int       cap_paths;
    int32     rn;
} PathAggregate;

typedef struct PathMaterialized
{
    hash128_t *ids;
    PathClaim *claims;
    int        n_claims;
    double     weight;
} PathMaterialized;

typedef struct PathEdgeBuf
{
    MemoryContext caller;
    PathEdge     *items;
    int           count;
    int           cap;
    uint64        seen;
} PathEdgeBuf;

typedef struct PathStepClaim
{
    hash128_t src_id;
    hash128_t entity;
    hash128_t p1;
    double    rating;
    double    deviation;
} PathStepClaim;

typedef struct PathStepScopedEntry
{
    hash128_t claim;
    double    rating;
    double    deviation;
} PathStepScopedEntry;

typedef struct PathStepPreviewEntry
{
    hash128_t entity;
    hash128_t predicate;
    hash128_t object;
    char     *type_label;
    bool      pred_null;
    bool      obj_null;
} PathStepPreviewEntry;

typedef struct PathStepCollectCtx
{
    MemoryContext    caller;
    const hash128_t *frontier_ids;
    int              n_frontier;
    bool             reverse;
    PathStepClaim   *items;
    int              count;
    int              cap;
    uint64           seen;
} PathStepCollectCtx;

typedef struct PathStepScopedCtx
{
    MemoryContext caller;
    HTAB         *map;
} PathStepScopedCtx;

typedef struct PathStepPreviewCtx
{
    MemoryContext caller;
    HTAB         *map;
} PathStepPreviewCtx;

static int
cmp_hash128(const void *pa, const void *pb)
{
    return memcmp(pa, pb, sizeof(hash128_t));
}

static int
cmp_path_edge_for_src(const void *pa, const void *pb)
{
    const PathEdge *a = (const PathEdge *) pa;
    const PathEdge *b = (const PathEdge *) pb;
    int             c;

    c = memcmp(&a->src_id, &b->src_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (a->weight != b->weight)
        return a->weight > b->weight ? -1 : 1; /* DESC */
    c = memcmp(&a->dst_id, &b->dst_id, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (a->has_claim != b->has_claim)
        return a->has_claim ? 1 : -1;
    if (a->has_claim)
        return memcmp(&a->claim.claim_id, &b->claim.claim_id, sizeof(hash128_t));
    return 0;
}

static void
find_path_edge_span(const PathEdge *edges, int n_edges, const hash128_t *src,
                    int *out_start, int *out_end)
{
    int lo = 0;
    int hi = n_edges;

    while (lo < hi)
    {
        int mid = lo + (hi - lo) / 2;
        if (memcmp(&edges[mid].src_id, src, sizeof(hash128_t)) < 0)
            lo = mid + 1;
        else
            hi = mid;
    }
    *out_start = lo;
    while (lo < n_edges && memcmp(&edges[lo].src_id, src, sizeof(hash128_t)) == 0)
        lo++;
    *out_end = lo;
}

static int
cmp_final_node_pair(const void *pa, const void *pb)
{
    const PathStepNode *a = *(const PathStepNode *const *) pa;
    const PathStepNode *b = *(const PathStepNode *const *) pb;
    int                 c;

    c = memcmp(&a->root, &b->root, sizeof(hash128_t));
    if (c != 0)
        return c;
    return memcmp(&a->cur, &b->cur, sizeof(hash128_t));
}

static int
cmp_agg_rank(const void *pa, const void *pb)
{
    const PathAggregate *a = (const PathAggregate *) pa;
    const PathAggregate *b = (const PathAggregate *) pb;
    int                  c;

    c = memcmp(&a->root, &b->root, sizeof(hash128_t));
    if (c != 0)
        return c;
    if (a->total_weight != b->total_weight)
        return a->total_weight > b->total_weight ? -1 : 1; /* DESC */
    return memcmp(&a->target, &b->target, sizeof(hash128_t));
}

static int
cmp_materialized_path(const void *pa, const void *pb, void *arg)
{
    int                     n_ids = *(const int *) arg;
    const PathMaterialized *a = (const PathMaterialized *) pa;
    const PathMaterialized *b = (const PathMaterialized *) pb;
    int                     c;

    c = memcmp(a->ids, b->ids, (Size) n_ids * sizeof(hash128_t));
    if (c != 0)
        return c;
    if (a->weight != b->weight)
        return a->weight > b->weight ? -1 : 1;
    if (a->n_claims != b->n_claims)
        return a->n_claims < b->n_claims ? -1 : 1;
    if (a->n_claims > 0)
        return memcmp(a->claims, b->claims, (Size) a->n_claims * sizeof(PathClaim));
    return 0;
}

static bool
visit_step_claim_raw(TupleTableSlot *slot, void *context)
{
    PathStepCollectCtx *ctx = (PathStepCollectCtx *) context;
    bool                isnull = false;
    Datum               d;
    int64               ord;
    hash128_t           src_b3;
    hash128_t           entity;
    lp_path             path;
    lp_id               parts[3];
    hash128_t           p1, p3;
    bool                ok;
    double              rating_d, dev_d;
    MemoryContext       prev;

    if ((++ctx->seen & 4095) == 0)
        CHECK_FOR_INTERRUPTS();

    d = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    ord = DatumGetInt64(d);
    if (ord < 1 || ord > ctx->n_frontier) return true;
    src_b3 = ctx->frontier_ids[ord - 1];

    d = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    path = laplace_pub_geo_path(d);
    if (lp_path_len(path) != 3 || lp_path_expand(path, parts, 3) != 3)
        return true;

    memcpy(&p1, parts[0].b, sizeof(hash128_t));
    memcpy(&p3, parts[2].b, sizeof(hash128_t));
    ok = ctx->reverse
        ? (memcmp(&p3, &src_b3, sizeof(hash128_t)) == 0)
        : (memcmp(&p1, &src_b3, sizeof(hash128_t)) == 0);
    if (!ok)
        return true;

    d = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    entity = laplace_pub_blake3_to_hash128(d);

    d = slot_getattr(slot, 4, &isnull);
    rating_d = isnull ? 0.0 : DatumGetFloat8(d);
    d = slot_getattr(slot, 5, &isnull);
    dev_d = isnull ? 0.0 : DatumGetFloat8(d);

    prev = MemoryContextSwitchTo(ctx->caller);
    if (ctx->count == ctx->cap)
    {
        int new_cap = ctx->cap == 0 ? 128 : ctx->cap * 2;
        ctx->items = ctx->items == NULL
            ? (PathStepClaim *) palloc((Size) new_cap * sizeof(PathStepClaim))
            : (PathStepClaim *) repalloc(ctx->items, (Size) new_cap * sizeof(PathStepClaim));
        ctx->cap = new_cap;
    }
    ctx->items[ctx->count++] = (PathStepClaim){
        .src_id    = src_b3,
        .entity    = entity,
        .p1        = p1,
        .rating    = rating_d,
        .deviation = dev_d
    };
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
visit_step_scoped(TupleTableSlot *slot, void *context)
{
    PathStepScopedCtx   *ctx = (PathStepScopedCtx *) context;
    bool                 isnull = false;
    Datum                d_claim, d_rat, d_dev;
    hash128_t            claim;
    double               rat, dev;
    PathStepScopedEntry *se;
    bool                 found = false;
    MemoryContext        prev;

    d_claim = slot_getattr(slot, 1, &isnull);
    if (isnull) return true;
    claim = laplace_pub_blake3_to_hash128(d_claim);

    d_rat = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    rat = DatumGetFloat8(d_rat);

    d_dev = slot_getattr(slot, 3, &isnull);
    if (isnull) return true;
    dev = DatumGetFloat8(d_dev);

    prev = MemoryContextSwitchTo(ctx->caller);
    se = (PathStepScopedEntry *) hash_search(ctx->map, &claim, HASH_ENTER, &found);
    if (!found)
    {
        se->rating    = rat;
        se->deviation = dev;
    }
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
visit_step_preview(TupleTableSlot *slot, void *context)
{
    PathStepPreviewCtx   *ctx = (PathStepPreviewCtx *) context;
    bool                  isnull = false;
    Datum                 d_ent, d_pred, d_obj, d_lbl;
    hash128_t             entity;
    PathStepPreviewEntry *pe;
    bool                  found = false;
    MemoryContext         prev;

    d_ent = slot_getattr(slot, 2, &isnull);
    if (isnull) return true;
    entity = laplace_pub_blake3_to_hash128(d_ent);

    prev = MemoryContextSwitchTo(ctx->caller);
    pe = (PathStepPreviewEntry *) hash_search(ctx->map, &entity, HASH_ENTER, &found);
    if (!found)
    {
        memset(pe, 0, sizeof(*pe));
        pe->entity = entity;

        d_pred = slot_getattr(slot, 3, &isnull);
        pe->pred_null = isnull;
        if (!isnull)
            pe->predicate = laplace_pub_blake3_to_hash128(d_pred);

        d_obj = slot_getattr(slot, 4, &isnull);
        pe->obj_null = isnull;
        if (!isnull)
            pe->object = laplace_pub_blake3_to_hash128(d_obj);

        d_lbl = slot_getattr(slot, 5, &isnull);
        if (!isnull)
            pe->type_label = TextDatumGetCString(d_lbl);
    }
    MemoryContextSwitchTo(prev);
    return true;
}

static bool
step_cstr_in_list(char *const *arr, int count, const char *target)
{
    if (target == NULL)
        return false;
    for (int i = 0; i < count; ++i)
    {
        if (arr[i] != NULL && strcmp(arr[i], target) == 0)
            return true;
    }
    return false;
}

static bool
step_id_in_list(const hash128_t *arr, int count, const hash128_t *target)
{
    for (int i = 0; i < count; ++i)
    {
        if (memcmp(&arr[i], target, sizeof(hash128_t)) == 0)
            return true;
    }
    return false;
}

static void
append_hex_bytea_json(StringInfo buf, const hash128_t *id)
{
    static const char hex[] = "0123456789abcdef";
    const uint8_t    *bytes = (const uint8_t *) id;

    appendStringInfoString(buf, "\"\\\\x");
    for (int i = 0; i < 16; ++i)
    {
        appendStringInfoCharMacro(buf, hex[(bytes[i] >> 4) & 0x0f]);
        appendStringInfoCharMacro(buf, hex[bytes[i] & 0x0f]);
    }
    appendStringInfoCharMacro(buf, '"');
}

static void
append_float8_json(StringInfo buf, double val)
{
    char *s;

    if (isnan(val) || isinf(val))
    {
        appendStringInfoString(buf, "null");
        return;
    }
    s = DatumGetCString(DirectFunctionCall1(float8out, Float8GetDatum(val)));
    appendStringInfoString(buf, s);
    pfree(s);
}

static PathStepSpec *
parse_steps_json(const char *steps_cstr, int *out_n_steps)
{
    Jsonb        *jb;
    int           n_steps;
    PathStepSpec *specs;

    jb = DatumGetJsonbP(DirectFunctionCall1(jsonb_in, CStringGetDatum(steps_cstr)));
    if (!JB_ROOT_IS_ARRAY(jb) || JB_ROOT_IS_SCALAR(jb))
        ereport(ERROR, (errmsg("generation.path_plane: steps must be a JSON array")));

    n_steps = (int) JsonContainerSize(&jb->root);
    *out_n_steps = n_steps;
    if (n_steps == 0)
        return NULL;

    specs = (PathStepSpec *) palloc0((Size) n_steps * sizeof(PathStepSpec));
    for (int s = 0; s < n_steps; ++s)
    {
        JsonbValue     *elem = getIthJsonbValueFromContainer(&jb->root, (uint32) s);
        JsonbContainer *obj;
        JsonbValue      rv_buf, ov_buf, tv_buf;
        JsonbValue     *rv, *ov, *tv;

        if (elem == NULL || elem->type != jbvBinary || !JsonContainerIsObject(elem->val.binary.data))
            ereport(ERROR, (errmsg("generation.path_plane: step %d must be a JSON object", s)));
        obj = elem->val.binary.data;

        rv = getKeyJsonValueFromContainer(obj, "reverse", 7, &rv_buf);
        if (rv != NULL && rv->type == jbvBool)
            specs[s].reverse = rv->val.boolean;
        else if (rv != NULL && rv->type == jbvString)
        {
            char *tmp = pnstrdup(rv->val.string.val, rv->val.string.len);
            specs[s].reverse = DatumGetBool(DirectFunctionCall1(boolin, CStringGetDatum(tmp)));
            pfree(tmp);
        }

        ov = getKeyJsonValueFromContainer(obj, "optional", 8, &ov_buf);
        if (ov != NULL && ov->type == jbvBool)
            specs[s].optional = ov->val.boolean;
        else if (ov != NULL && ov->type == jbvString)
        {
            char *tmp = pnstrdup(ov->val.string.val, ov->val.string.len);
            specs[s].optional = DatumGetBool(DirectFunctionCall1(boolin, CStringGetDatum(tmp)));
            pfree(tmp);
        }

        tv = getKeyJsonValueFromContainer(obj, "types", 5, &tv_buf);
        if (tv != NULL && tv->type == jbvBinary && JsonContainerIsArray(tv->val.binary.data))
        {
            JsonbContainer *arr = tv->val.binary.data;
            int             nt = (int) JsonContainerSize(arr);

            specs[s].n_types = nt;
            if (nt == 0)
            {
                specs[s].types_arr = construct_empty_array(TEXTOID);
            }
            else
            {
                Datum *td = (Datum *) palloc((Size) nt * sizeof(Datum));
                for (int i = 0; i < nt; ++i)
                {
                    JsonbValue *sv = getIthJsonbValueFromContainer(arr, (uint32) i);
                    if (sv == NULL || sv->type != jbvString)
                        ereport(ERROR, (errmsg("generation.path_plane: step %d types must be strings", s)));
                    td[i] = PointerGetDatum(cstring_to_text_with_len(sv->val.string.val, sv->val.string.len));
                    pfree(sv);
                }
                specs[s].types_arr = construct_array(td, nt, TEXTOID, -1, false, TYPALIGN_INT);
                for (int i = 0; i < nt; ++i)
                    pfree(DatumGetPointer(td[i]));
                pfree(td);
            }
        }
        else
        {
            specs[s].n_types   = 0;
            specs[s].types_arr = construct_empty_array(TEXTOID);
        }
        pfree(elem);
    }

    return specs;
}

PG_FUNCTION_INFO_V1(pg_laplace_path_plane);

Datum
pg_laplace_path_plane(PG_FUNCTION_ARGS)
{
    MemoryContext   caller = CurrentMemoryContext;
    ReturnSetInfo  *rsinfo;
    ArrayType      *roots_arr;
    char           *steps_cstr;
    int32           fanout;
    int32           cap;
    bool            visible_only;
    ArrayType      *sources_arr;
    PathStepSpec   *steps;
    int             n_steps = 0;
    Datum          *r_elems;
    bool           *r_nulls;
    int             r_count;
    hash128_t      *root_ids;
    int             n_roots = 0;
    HTAB           *root_set = NULL;
    PathStepNode  **levels;
    int            *level_counts;
    int             n_final;
    PathStepNode  **final_ptrs;
    int             n_diff = 0;
    PathAggregate  *aggs;
    int             n_aggs = 0;
    int             agg_cap = 64;
    StringInfoData  json_buf;

    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2) || PG_ARGISNULL(3))
        return (Datum) 0;

    fanout = PG_GETARG_INT32(2);
    cap    = PG_GETARG_INT32(3);
    if (fanout <= 0 || cap <= 0)
        return (Datum) 0;

    visible_only = PG_ARGISNULL(4) ? false : PG_GETARG_BOOL(4);
    sources_arr  = PG_ARGISNULL(5) ? NULL : PG_GETARG_ARRAYTYPE_P(5);

    steps_cstr = text_to_cstring(PG_GETARG_TEXT_PP(1));
    steps      = parse_steps_json(steps_cstr, &n_steps);
    pfree(steps_cstr);
    if (n_steps == 0)
        return (Datum) 0;

    roots_arr = PG_GETARG_ARRAYTYPE_P(0);
    deconstruct_array(roots_arr, BYTEAOID, -1, false, TYPALIGN_INT, &r_elems, &r_nulls, &r_count);
    if (r_count == 0)
    {
        pfree(r_elems);
        pfree(r_nulls);
        return (Datum) 0;
    }

    root_ids = (hash128_t *) palloc((Size) r_count * sizeof(hash128_t));
    for (int i = 0; i < r_count; ++i)
    {
        if (!r_nulls[i])
            root_ids[n_roots++] = datum_to_hash128(r_elems[i]);
    }
    pfree(r_elems);
    pfree(r_nulls);
    if (n_roots == 0)
    {
        pfree(root_ids);
        return (Datum) 0;
    }

    qsort(root_ids, (Size) n_roots, sizeof(hash128_t), cmp_hash128);
    {
        int w = 1;
        for (int i = 1; i < n_roots; ++i)
        {
            if (memcmp(&root_ids[w - 1], &root_ids[i], sizeof(hash128_t)) != 0)
                root_ids[w++] = root_ids[i];
        }
        n_roots = w;
    }

    if (visible_only)
    {
        HASHCTL hctl;
        memset(&hctl, 0, sizeof(hctl));
        hctl.keysize   = sizeof(hash128_t);
        hctl.entrysize = sizeof(hash128_t);
        hctl.hcxt      = caller;
        root_set = hash_create("path_plane roots", Max(n_roots, 16), &hctl,
                               HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        for (int i = 0; i < n_roots; ++i)
            (void) hash_search(root_set, &root_ids[i], HASH_ENTER, NULL);
    }

    levels       = (PathStepNode **) palloc0((Size) (n_steps + 1) * sizeof(PathStepNode *));
    level_counts = (int *) palloc0((Size) (n_steps + 1) * sizeof(int));

    levels[0]       = (PathStepNode *) palloc0((Size) n_roots * sizeof(PathStepNode));
    level_counts[0] = n_roots;
    for (int i = 0; i < n_roots; ++i)
    {
        levels[0][i].cur        = root_ids[i];
        levels[0][i].root       = root_ids[i];
        levels[0][i].weight     = 1.0;
        levels[0][i].parent_idx = -1;
        levels[0][i].has_claim  = false;
    }
    pfree(root_ids);

    for (int s = 0; s < n_steps; ++s)
    {
        int           n_cur = level_counts[s];
        PathStepNode *cur_nodes = levels[s];
        hash128_t    *frontier_ids;
        int           n_frontier;
        PathEdgeBuf   ebuf;
        PathEdge     *all_edges = NULL;
        int           n_edges = 0;
        PathStepNode *next_nodes = NULL;
        int           next_count = 0;
        int           next_cap = 0;

        if (n_cur == 0)
            return (Datum) 0;

        CHECK_FOR_INTERRUPTS();

        frontier_ids = (hash128_t *) palloc((Size) n_cur * sizeof(hash128_t));
        for (int i = 0; i < n_cur; ++i)
            frontier_ids[i] = cur_nodes[i].cur;
        qsort(frontier_ids, (Size) n_cur, sizeof(hash128_t), cmp_hash128);
        n_frontier = 1;
        for (int i = 1; i < n_cur; ++i)
        {
            if (memcmp(&frontier_ids[n_frontier - 1], &frontier_ids[i], sizeof(hash128_t)) != 0)
                frontier_ids[n_frontier++] = frontier_ids[i];
        }

        ebuf.caller = caller;
        ebuf.items  = NULL;
        ebuf.count  = 0;
        ebuf.cap    = 0;
        ebuf.seen   = 0;

        if (steps[s].n_types > 0)
        {
            ArrayType          *frontier_b3 = laplace_pub_blake3_array(frontier_ids, n_frontier);
            Oid                 b3_arr_oid = laplace_pub_blake3_array_oid();
            Oid                 claim_argtypes[5] = {
                b3_arr_oid, INT8OID, INT2ARRAYOID, b3_arr_oid, INT2OID
            };
            ArrayType          *empty_int2 = construct_empty_array(INT2OID);
            ArrayType          *empty_b3 = laplace_pub_blake3_array(NULL, 0);
            Datum               claim_args[5] = {
                PointerGetDatum(frontier_b3), (Datum) 0,
                PointerGetDatum(empty_int2), PointerGetDatum(empty_b3),
                Int16GetDatum(steps[s].reverse ? 2 : 1)
            };
            bool                claim_nulls[5] = { false, true, false, false, false };
            PathStepCollectCtx  collect;

            memset(&collect, 0, sizeof(collect));
            collect.caller       = caller;
            collect.frontier_ids = frontier_ids;
            collect.n_frontier   = n_frontier;
            collect.reverse      = steps[s].reverse;

            (void) laplace_pub_call_srf("public", "laplace_claims_each", 5,
                                        claim_argtypes, claim_args, claim_nulls,
                                        visit_step_claim_raw, &collect);
            pfree(frontier_b3);
            pfree(empty_int2);
            pfree(empty_b3);

            if (collect.count > 0)
            {
                hash128_t  *claim_ids = (hash128_t *) palloc((Size) collect.count * sizeof(hash128_t));
                int         n_claims;
                ArrayType  *claim_b3_arr;
                HTAB       *scoped_map = NULL;
                HTAB       *preview_map;
                Datum      *t_elems = NULL;
                bool       *t_nulls = NULL;
                int         t_raw = 0;
                char      **type_strs;
                hash128_t  *tids;
                int         n_tids = 0;
                HASHCTL     hctl;

                for (int ci = 0; ci < collect.count; ++ci)
                    claim_ids[ci] = collect.items[ci].entity;
                qsort(claim_ids, (Size) collect.count, sizeof(hash128_t), cmp_hash128);
                n_claims = 1;
                for (int ci = 1; ci < collect.count; ++ci)
                    if (memcmp(&claim_ids[n_claims - 1], &claim_ids[ci], sizeof(hash128_t)) != 0)
                        claim_ids[n_claims++] = claim_ids[ci];

                claim_b3_arr = laplace_pub_blake3_array(claim_ids, n_claims);

                if (sources_arr != NULL)
                {
                    ArrayType        *sources_b3 = laplace_pub_bytea_array_to_blake3_array(sources_arr);
                    Oid               sc_types[2] = { b3_arr_oid, b3_arr_oid };
                    Datum             sc_args[2] = { PointerGetDatum(claim_b3_arr), PointerGetDatum(sources_b3) };
                    bool              sc_nulls[2] = { false, false };
                    PathStepScopedCtx sc_ctx;

                    memset(&hctl, 0, sizeof(hctl));
                    hctl.keysize   = sizeof(hash128_t);
                    hctl.entrysize = sizeof(PathStepScopedEntry);
                    hctl.hcxt      = caller;
                    scoped_map = hash_create("path_plane scoped", Max(n_claims, 16), &hctl,
                                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                    sc_ctx.caller = caller;
                    sc_ctx.map    = scoped_map;
                    (void) laplace_pub_call_srf("public", "laplace_source_standings", 2,
                                                sc_types, sc_args, sc_nulls,
                                                visit_step_scoped, &sc_ctx);
                    pfree(sources_b3);
                }

                memset(&hctl, 0, sizeof(hctl));
                hctl.keysize   = sizeof(hash128_t);
                hctl.entrysize = sizeof(PathStepPreviewEntry);
                hctl.hcxt      = caller;
                preview_map = hash_create("path_plane preview", Max(n_claims, 16), &hctl,
                                          HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                {
                    Datum              pr_args[1] = { PointerGetDatum(claim_b3_arr) };
                    bool               pr_nulls[1] = { false };
                    PathStepPreviewCtx pr_ctx = { caller, preview_map };

                    (void) laplace_pub_call_srf("public", "laplace_claim_preview", 1,
                                                &b3_arr_oid, pr_args, pr_nulls,
                                                visit_step_preview, &pr_ctx);
                }

                deconstruct_array(steps[s].types_arr, TEXTOID, -1, false, TYPALIGN_INT,
                                  &t_elems, &t_nulls, &t_raw);
                type_strs = (char **) palloc0((Size) Max(t_raw, 1) * sizeof(char *));
                tids      = (hash128_t *) palloc((Size) Max(t_raw, 1) * sizeof(hash128_t));
                for (int ti = 0; ti < t_raw; ++ti)
                {
                    if (!t_nulls[ti])
                    {
                        text *tx = DatumGetTextPP(t_elems[ti]);
                        type_strs[ti] = text_to_cstring(tx);
                        if (laplace_pub_text_id_hash(VARDATA_ANY(tx), VARSIZE_ANY_EXHDR(tx), &tids[n_tids]))
                            n_tids++;
                    }
                }
                pfree(t_elems);
                pfree(t_nulls);

                for (int ci = 0; ci < collect.count; ++ci)
                {
                    const PathStepClaim        *sc = &collect.items[ci];
                    PathStepPreviewEntry       *pe;
                    double                      eff_rating = sc->rating;
                    double                      eff_dev    = sc->deviation;
                    double                      w;
                    bool                        match = false;
                    PathEdge                    edge;

                    if (scoped_map != NULL)
                    {
                        PathStepScopedEntry *se = (PathStepScopedEntry *) hash_search(
                            scoped_map, &sc->entity, HASH_FIND, NULL);
                        if (se == NULL)
                            continue;
                        eff_rating = se->rating;
                        eff_dev    = se->deviation;
                    }

                    pe = (PathStepPreviewEntry *) hash_search(preview_map, &sc->entity, HASH_FIND, NULL);
                    if (pe == NULL || pe->pred_null || pe->obj_null)
                        continue;

                    if (step_id_in_list(tids, n_tids, &pe->predicate) ||
                        step_cstr_in_list(type_strs, t_raw, pe->type_label))
                    {
                        match = true;
                    }
                    else
                    {
                        const char *canon_name = NULL;
                        bool        m_inv = false;
                        int16       m_qual = -1;
                        if (laplace_relation_metadata_lookup_c(&pe->predicate, NULL,
                                                               &canon_name, &m_inv, &m_qual) &&
                            !m_inv && m_qual < 0 &&
                            step_cstr_in_list(type_strs, t_raw, canon_name))
                            match = true;
                    }
                    if (!match)
                        continue;

                    w = laplace_walk_edge_weight(llround(eff_rating * 1e9),
                                                 llround(eff_dev * 1e9));
                    if (isnan(w) || !(w > 0.0))
                        continue;

                    memset(&edge, 0, sizeof(edge));
                    edge.src_id           = sc->src_id;
                    edge.dst_id           = steps[s].reverse ? sc->p1 : pe->object;
                    edge.weight           = w;
                    edge.has_claim        = true;
                    edge.claim.claim_id   = sc->entity;
                    edge.claim.subject_id = sc->p1;
                    edge.claim.type_id    = pe->predicate;
                    edge.claim.object_id  = pe->object;
                    edge.claim.rating     = eff_rating;
                    edge.claim.rd         = eff_dev;

                    if (ebuf.count == ebuf.cap)
                    {
                        int new_cap = ebuf.cap == 0 ? 128 : ebuf.cap * 2;
                        ebuf.items  = ebuf.items == NULL
                            ? (PathEdge *) palloc((Size) new_cap * sizeof(PathEdge))
                            : (PathEdge *) repalloc(ebuf.items, (Size) new_cap * sizeof(PathEdge));
                        ebuf.cap    = new_cap;
                    }
                    ebuf.items[ebuf.count++] = edge;
                }

                for (int ti = 0; ti < t_raw; ++ti)
                    if (type_strs[ti]) pfree(type_strs[ti]);
                pfree(type_strs);
                pfree(tids);
                {
                    HASH_SEQ_STATUS       pseq;
                    PathStepPreviewEntry *pe_it;
                    hash_seq_init(&pseq, preview_map);
                    while ((pe_it = (PathStepPreviewEntry *) hash_seq_search(&pseq)) != NULL)
                        if (pe_it->type_label) pfree(pe_it->type_label);
                }
                hash_destroy(preview_map);
                if (scoped_map != NULL)
                    hash_destroy(scoped_map);
                pfree(claim_b3_arr);
                pfree(claim_ids);
                pfree(collect.items);
            }
        }

        if (steps[s].optional)
        {
            int extra = n_frontier;
            int need = ebuf.count + extra;
            if (need > ebuf.cap)
            {
                ebuf.cap   = Max(need, 64);
                ebuf.items = ebuf.items == NULL
                    ? (PathEdge *) palloc((Size) ebuf.cap * sizeof(PathEdge))
                    : (PathEdge *) repalloc(ebuf.items, (Size) ebuf.cap * sizeof(PathEdge));
            }
            for (int fi = 0; fi < n_frontier; ++fi)
            {
                PathEdge opt;
                memset(&opt, 0, sizeof(opt));
                opt.src_id    = frontier_ids[fi];
                opt.dst_id    = frontier_ids[fi];
                opt.weight    = 1.0;
                opt.has_claim = false;
                ebuf.items[ebuf.count++] = opt;
            }
        }
        pfree(frontier_ids);

        if (ebuf.count == 0)
            return (Datum) 0;

        all_edges = ebuf.items;
        n_edges   = ebuf.count;
        qsort(all_edges, (Size) n_edges, sizeof(PathEdge), cmp_path_edge_for_src);

        /* Retain top `fanout` edges per distinct src_id in-place. */
        {
            int kept = 0;
            int per_src = 0;
            for (int i = 0; i < n_edges; ++i)
            {
                if (i == 0 || memcmp(&all_edges[i].src_id, &all_edges[i - 1].src_id, sizeof(hash128_t)) != 0)
                    per_src = 1;
                else
                    per_src++;
                if (per_src <= fanout)
                    all_edges[kept++] = all_edges[i];
            }
            n_edges = kept;
        }

        for (int i = 0; i < n_cur; ++i)
        {
            const PathStepNode *prev = &cur_nodes[i];
            int                 span_start = 0;
            int                 span_end = 0;

            find_path_edge_span(all_edges, n_edges, &prev->cur, &span_start, &span_end);
            for (int ei = span_start; ei < span_end; ++ei)
            {
                if (next_count == next_cap)
                {
                    next_cap   = next_cap == 0 ? 128 : next_cap * 2;
                    next_nodes = next_nodes == NULL
                        ? (PathStepNode *) palloc((Size) next_cap * sizeof(PathStepNode))
                        : (PathStepNode *) repalloc(next_nodes, (Size) next_cap * sizeof(PathStepNode));
                }
                next_nodes[next_count].cur        = all_edges[ei].dst_id;
                next_nodes[next_count].root       = prev->root;
                next_nodes[next_count].weight     = prev->weight * all_edges[ei].weight;
                next_nodes[next_count].parent_idx = i;
                next_nodes[next_count].has_claim  = all_edges[ei].has_claim;
                next_nodes[next_count].claim      = all_edges[ei].claim;
                next_count++;
            }
        }

        pfree(all_edges);
        if (next_count == 0)
            return (Datum) 0;

        levels[s + 1]       = next_nodes;
        level_counts[s + 1] = next_count;
    }

    n_final = level_counts[n_steps];
    if (n_final == 0)
        return (Datum) 0;

    final_ptrs = (PathStepNode **) palloc((Size) n_final * sizeof(PathStepNode *));
    for (int i = 0; i < n_final; ++i)
    {
        PathStepNode *pn = &levels[n_steps][i];
        if (memcmp(&pn->root, &pn->cur, sizeof(hash128_t)) == 0)
            continue;
        if (visible_only && hash_search(root_set, &pn->cur, HASH_FIND, NULL) == NULL)
            continue;
        final_ptrs[n_diff++] = pn;
    }

    if (n_diff == 0)
        return (Datum) 0;

    qsort(final_ptrs, (Size) n_diff, sizeof(PathStepNode *), cmp_final_node_pair);

    aggs = (PathAggregate *) palloc((Size) agg_cap * sizeof(PathAggregate));
    for (int i = 0; i < n_diff;)
    {
        const hash128_t *root = &final_ptrs[i]->root;
        const hash128_t *target = &final_ptrs[i]->cur;
        int              j = i;
        PathAggregate   *a;

        while (j < n_diff &&
               memcmp(&final_ptrs[j]->root, root, sizeof(hash128_t)) == 0 &&
               memcmp(&final_ptrs[j]->cur, target, sizeof(hash128_t)) == 0)
            j++;

        if (n_aggs == agg_cap)
        {
            agg_cap *= 2;
            aggs = (PathAggregate *) repalloc(aggs, (Size) agg_cap * sizeof(PathAggregate));
        }
        a = &aggs[n_aggs++];
        a->root         = *root;
        a->target       = *target;
        a->total_weight = 0.0;
        a->n_paths      = j - i;
        a->cap_paths    = j - i;
        a->path_indices = (int *) palloc((Size) a->n_paths * sizeof(int));
        a->rn           = 0;
        for (int k = 0; k < a->n_paths; ++k)
        {
            int idx = (int) (final_ptrs[i + k] - levels[n_steps]);
            a->path_indices[k]  = idx;
            a->total_weight    += final_ptrs[i + k]->weight;
        }
        i = j;
    }

    qsort(aggs, (Size) n_aggs, sizeof(PathAggregate), cmp_agg_rank);

    initStringInfo(&json_buf);
    for (int i = 0, rn = 0; i < n_aggs; ++i)
    {
        PathAggregate    *a = &aggs[i];
        PathMaterialized *mats;
        int               n_ids = n_steps + 1;
        Datum             vals[4];
        bool              nulls[4] = { false, false, false, false };
        Datum             jb_datum;

        if (i == 0 || memcmp(&a->root, &aggs[i - 1].root, sizeof(hash128_t)) != 0)
            rn = 1;
        else
            rn++;
        if (rn > cap)
            continue;

        CHECK_FOR_INTERRUPTS();

        mats = (PathMaterialized *) palloc((Size) a->n_paths * sizeof(PathMaterialized));
        for (int k = 0; k < a->n_paths; ++k)
        {
            int node_idx = a->path_indices[k];
            int nc = 0;

            mats[k].ids    = (hash128_t *) palloc((Size) n_ids * sizeof(hash128_t));
            mats[k].claims = (PathClaim *) palloc((Size) n_steps * sizeof(PathClaim));
            mats[k].weight = levels[n_steps][node_idx].weight;

            {
                PathClaim rev_claims[64];
                PathClaim *dyn_rev = n_steps > 64
                    ? (PathClaim *) palloc((Size) n_steps * sizeof(PathClaim))
                    : rev_claims;
                int cur_idx = node_idx;

                for (int step = n_steps; step >= 0; --step)
                {
                    const PathStepNode *st = &levels[step][cur_idx];
                    mats[k].ids[step] = st->cur;
                    if (step > 0 && st->has_claim)
                        dyn_rev[nc++] = st->claim;
                    cur_idx = st->parent_idx;
                }
                for (int ci = 0; ci < nc; ++ci)
                    mats[k].claims[ci] = dyn_rev[nc - 1 - ci];
                mats[k].n_claims = nc;
                if (dyn_rev != rev_claims)
                    pfree(dyn_rev);
            }
        }

        qsort_arg(mats, (Size) a->n_paths, sizeof(PathMaterialized),
                  cmp_materialized_path, &n_ids);

        resetStringInfo(&json_buf);
        appendStringInfoChar(&json_buf, '[');
        for (int k = 0; k < a->n_paths; ++k)
        {
            if (k > 0)
                appendStringInfoChar(&json_buf, ',');
            appendStringInfoString(&json_buf, "{\"ids\":[");
            for (int ii = 0; ii < n_ids; ++ii)
            {
                if (ii > 0)
                    appendStringInfoChar(&json_buf, ',');
                append_hex_bytea_json(&json_buf, &mats[k].ids[ii]);
            }
            appendStringInfoString(&json_buf, "],\"claims\":[");
            for (int ci = 0; ci < mats[k].n_claims; ++ci)
            {
                const PathClaim *cl = &mats[k].claims[ci];
                if (ci > 0)
                    appendStringInfoChar(&json_buf, ',');
                appendStringInfoString(&json_buf, "{\"claim_id\":");
                append_hex_bytea_json(&json_buf, &cl->claim_id);
                appendStringInfoString(&json_buf, ",\"subject_id\":");
                append_hex_bytea_json(&json_buf, &cl->subject_id);
                appendStringInfoString(&json_buf, ",\"type_id\":");
                append_hex_bytea_json(&json_buf, &cl->type_id);
                appendStringInfoString(&json_buf, ",\"object_id\":");
                append_hex_bytea_json(&json_buf, &cl->object_id);
                appendStringInfoString(&json_buf, ",\"rating\":");
                append_float8_json(&json_buf, cl->rating);
                appendStringInfoString(&json_buf, ",\"rd\":");
                append_float8_json(&json_buf, cl->rd);
                appendStringInfoChar(&json_buf, '}');
            }
            appendStringInfoString(&json_buf, "],\"weight\":");
            append_float8_json(&json_buf, mats[k].weight);
            appendStringInfoChar(&json_buf, '}');

            pfree(mats[k].ids);
            pfree(mats[k].claims);
        }
        appendStringInfoChar(&json_buf, ']');
        pfree(mats);

        jb_datum = DirectFunctionCall1(jsonb_in, CStringGetDatum(json_buf.data));
        vals[0] = hash128_to_datum(&a->root);
        vals[1] = hash128_to_datum(&a->target);
        vals[2] = Float8GetDatum(a->total_weight);
        vals[3] = jb_datum;
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, vals, nulls);
    }

    pfree(json_buf.data);
    return (Datum) 0;
}
