#ifndef LAPLACE_PROMPT_INTENT_GEOMETRY_H
#define LAPLACE_PROMPT_INTENT_GEOMETRY_H

#include "port/pg_bswap.h"
#include "laplace/laplace.h"
#include "public_direct.h"
#include "trajectory_wkb.h"

static inline int
laplace_prompt_hilbert_compare(const void *left, const void *right)
{
    const LaplacePromptHilbertCandidate *a = left, *b = right;
    int order = memcmp(a->delta, b->delta, sizeof(a->delta));
    return order ? order : memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static inline void
laplace_prompt_hilbert_delta(const uint8 left[16], const uint8 right[16],
                             uint8 out[16])
{
    const uint8 *high = left, *low = right;
    int borrow = 0;
    if (memcmp(left, right, 16) < 0)
    {
        high = right;
        low = left;
    }
    for (int i = 15; i >= 0; --i)
    {
        int value = (int) high[i] - (int) low[i] - borrow;
        if (value < 0)
        {
            value += 256;
            borrow = 1;
        }
        else
            borrow = 0;
        out[i] = (uint8) value;
    }
}

static inline void
laplace_prompt_geometry_append(LaplacePromptIntent *intent,
                               const hash128_t *source, const hash128_t *id,
                               uint32 plane, uint32 rank,
                               const uint8 *hilbert_delta, double distance)
{
    if (plane < LAPLACE_PROMPT_GEOMETRY_HILBERT ||
        plane > LAPLACE_PROMPT_GEOMETRY_FRECHET ||
        rank == 0 ||
        (plane == LAPLACE_PROMPT_GEOMETRY_HILBERT && !hilbert_delta) ||
        (plane != LAPLACE_PROMPT_GEOMETRY_HILBERT &&
         (!isfinite(distance) || distance < 0.0)))
        elog(ERROR, "prompt geometry: invalid deterministic metric response");
    if (intent->geometry_count == intent->geometry_capacity)
    {
        int64 capacity = intent->geometry_capacity ?
            (int64) intent->geometry_capacity * 2 : 32;
        if (capacity > INT_MAX ||
            (Size) capacity > MaxAllocSize / sizeof(LaplacePromptGeometryCandidate))
            elog(ERROR, "prompt geometry: response set exceeds allocation capacity");
        intent->geometry = intent->geometry ?
            repalloc(intent->geometry,
                     sizeof(*intent->geometry) * (Size) capacity) :
            palloc(sizeof(*intent->geometry) * (Size) capacity);
        intent->geometry_capacity = (int) capacity;
    }
    LaplacePromptGeometryCandidate *target =
        &intent->geometry[intent->geometry_count++];
    MemSet(target, 0, sizeof(*target));
    target->source = *source;
    target->id = *id;
    target->plane = plane;
    target->rank = rank;
    target->distance = distance;
    if (hilbert_delta)
        memcpy(target->hilbert_delta, hilbert_delta, sizeof(target->hilbert_delta));
}

typedef struct PromptAngularCtx
{
    MemoryContext        owner;
    LaplacePromptIntent *intent;
    const hash128_t     *source;
    int                  fanout;
    hash128_t           *root_angular;
    int                 *root_angular_count;
    uint32               rank;
} PromptAngularCtx;

static bool
visit_prompt_angular(TupleTableSlot *slot, void *vctx)
{
    PromptAngularCtx *ctx = (PromptAngularCtx *) vctx;
    bool              id_null = false;
    bool              dist_null = false;
    Datum             d_id = slot_getattr(slot, 2, &id_null);
    Datum             d_dist = slot_getattr(slot, 5, &dist_null);
    hash128_t         id;
    double            distance;
    MemoryContext     prev;

    if (id_null || dist_null)
        return true;

    id = laplace_pub_blake3_to_hash128(d_id);
    distance = DatumGetFloat8(d_dist);
    ctx->rank++;

    prev = MemoryContextSwitchTo(ctx->owner);
    laplace_prompt_geometry_append(
        ctx->intent, ctx->source, &id, LAPLACE_PROMPT_GEOMETRY_ANGULAR,
        ctx->rank, NULL, distance);
    if (ctx->root_angular && ctx->root_angular_count &&
        hash128_eq(ctx->source, &ctx->intent->root) &&
        *ctx->root_angular_count < ctx->fanout)
        ctx->root_angular[(*ctx->root_angular_count)++] = id;
    MemoryContextSwitchTo(prev);
    return true;
}

typedef struct PromptHilbertCtx
{
    MemoryContext                 owner;
    LaplacePromptHilbertCandidate *items;
    int                           count;
    int                           cap;
} PromptHilbertCtx;

static bool
visit_prompt_hilbert(TupleTableSlot *slot, void *vctx)
{
    PromptHilbertCtx *ctx = (PromptHilbertCtx *) vctx;
    bool              id_null = false, key_null = false, anc_null = false;
    Datum             d_id = slot_getattr(slot, 1, &id_null);
    Datum             d_key = slot_getattr(slot, 2, &key_null);
    Datum             d_anc = slot_getattr(slot, 3, &anc_null);
    int64             cand_h, anchor_h;
    uint64            raw_ah, raw_ch;
    uint8             left[16] = {0}, right[16] = {0};
    LaplacePromptHilbertCandidate item;
    MemoryContext     prev;

    if (id_null || key_null || anc_null)
        elog(ERROR, "prompt geometry: indexed Hilbert row contains NULL");

    cand_h = DatumGetInt64(d_key);
    anchor_h = DatumGetInt64(d_anc);
    raw_ah = pg_hton64(((uint64) anchor_h) ^ UINT64CONST(0x8000000000000000));
    raw_ch = pg_hton64(((uint64) cand_h) ^ UINT64CONST(0x8000000000000000));
    memcpy(left + 8, &raw_ah, 8);
    memcpy(right + 8, &raw_ch, 8);

    item.id = laplace_pub_blake3_to_hash128(d_id);
    laplace_prompt_hilbert_delta(left, right, item.delta);

    prev = MemoryContextSwitchTo(ctx->owner);
    if (ctx->count == ctx->cap)
    {
        int new_cap = ctx->cap == 0 ? 32 : ctx->cap * 2;
        if ((Size) new_cap > MaxAllocSize / sizeof(*ctx->items))
            elog(ERROR, "prompt geometry: Hilbert candidate set exceeds allocation capacity");
        ctx->items = ctx->items == NULL
            ? palloc(sizeof(*ctx->items) * (Size) new_cap)
            : repalloc(ctx->items, sizeof(*ctx->items) * (Size) new_cap);
        ctx->cap = new_cap;
    }
    ctx->items[ctx->count++] = item;
    MemoryContextSwitchTo(prev);
    return true;
}

/* Geometry response of one anchor: an indexed S3 angular KNN over same-tier
 * points, then at most fanout equal, predecessor and successor Hilbert keys
 * each from the btree, ranked in C by exact unsigned Native-key delta. Direct
 * fmgr calls per anchor without SPI planning. */
static inline void
laplace_prompt_geometry_scan_anchor(
    LaplacePromptIntent *intent, const hash128_t *source, uint32 node,
    int fanout, hash128_t *root_angular, int *root_angular_count)
{
    const double    *coords = tier_tree_coord_array(intent->input->tree);
    const uint8     *tiers = tier_tree_tier_array(intent->input->tree);
    double           xyzm[4] = {
        coords[(Size) node * 4 + 0],
        coords[(Size) node * 4 + 1],
        coords[(Size) node * 4 + 2],
        coords[(Size) node * 4 + 3]
    };
    Datum            pt_geom = laplace_pub_point4d_geom(xyzm);
    Datum            excl_b3 = laplace_pub_hash128_to_blake3(source);
    Oid              geom_oid = laplace_pub_geometry_oid();
    Oid              b3_oid = laplace_pub_blake3_oid();
    Oid              knn_types[6] = { geom_oid, INT4OID, BOOLOID, INT2OID, INT2OID, b3_oid };
    Datum            knn_args[6] = {
        pt_geom,
        Int32GetDatum(Max(fanout, 0)),
        BoolGetDatum(true),
        Int16GetDatum((int16) tiers[node]),
        Int16GetDatum((int16) tiers[node]),
        excl_b3
    };
    bool             knn_nulls[6] = { false, false, false, false, false, false };
    PromptAngularCtx actx = {
        intent->owner, intent, source, fanout, root_angular, root_angular_count, 0
    };
    Oid              hilb_types[4] = { geom_oid, b3_oid, INT4OID, INT2OID };
    Datum            hilb_args[4] = {
        pt_geom,
        excl_b3,
        Int32GetDatum(fanout),
        Int16GetDatum((int16) tiers[node])
    };
    bool             hilb_nulls[4] = { false, false, false, false };
    PromptHilbertCtx hctx = { intent->owner, NULL, 0, 0 };

    (void) laplace_pub_call_srf("public", "laplace_knn4d", 6, knn_types,
                                knn_args, knn_nulls, visit_prompt_angular, &actx);

    (void) laplace_pub_call_srf("public", "laplace_hilbert_neighbors", 4, hilb_types,
                                hilb_args, hilb_nulls, visit_prompt_hilbert, &hctx);

    if (hctx.count > 1)
        qsort(hctx.items, (Size) hctx.count, sizeof(*hctx.items), laplace_prompt_hilbert_compare);
    for (int row = 0; row < Min(hctx.count, fanout); ++row)
        laplace_prompt_geometry_append(
            intent, source, &hctx.items[row].id, LAPLACE_PROMPT_GEOMETRY_HILBERT,
            (uint32) row + 1u, hctx.items[row].delta, 0.0);

    if (hctx.items) pfree(hctx.items);
    pfree(DatumGetPointer(pt_geom));
    pfree(DatumGetPointer(excl_b3));
}

typedef struct PromptFrechetScored
{
    hash128_t id;
    double    d;
} PromptFrechetScored;

typedef struct PromptFrechetCtx
{
    MemoryContext        owner;
    const double        *anchor_pts;
    int                  point_count;
    PromptFrechetScored *items;
    int                  count;
    int                  cap;
} PromptFrechetCtx;

static int
laplace_prompt_frechet_compare(const void *left, const void *right)
{
    const PromptFrechetScored *a = (const PromptFrechetScored *) left;
    const PromptFrechetScored *b = (const PromptFrechetScored *) right;

    if (a->d != b->d)
        return a->d < b->d ? -1 : 1;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static bool
visit_prompt_frechet_curve(TupleTableSlot *slot, void *vctx)
{
    PromptFrechetCtx    *ctx = (PromptFrechetCtx *) vctx;
    bool                 id_null = false, wkb_null = false;
    Datum                d_id = slot_getattr(slot, 2, &id_null);
    Datum                d_wkb = slot_getattr(slot, 3, &wkb_null);
    bytea               *wkb;
    uint32               n_pts = 0;
    const unsigned char *raw;
    double               d;
    MemoryContext        prev;

    if (id_null || wkb_null)
        return true;

    wkb = DatumGetByteaPP(d_wkb);
    raw = laplace_trajectory_wkb_points(wkb, &n_pts);
    if (n_pts == 0)
        return true;

    d = lp_frechet4((const double *) raw, (size_t) n_pts,
                    ctx->anchor_pts, (size_t) ctx->point_count);
    if (isnan(d) || !isfinite(d) || d < 0.0)
        return true;

    prev = MemoryContextSwitchTo(ctx->owner);
    if (ctx->count == ctx->cap)
    {
        int new_cap = ctx->cap == 0 ? 32 : ctx->cap * 2;
        if ((Size) new_cap > MaxAllocSize / sizeof(*ctx->items))
            elog(ERROR, "prompt geometry: Frechet candidate set exceeds allocation capacity");
        ctx->items = ctx->items == NULL
            ? palloc(sizeof(*ctx->items) * (Size) new_cap)
            : repalloc(ctx->items, sizeof(*ctx->items) * (Size) new_cap);
        ctx->cap = new_cap;
    }
    ctx->items[ctx->count++] = (PromptFrechetScored){
        .id = laplace_pub_blake3_to_hash128(d_id),
        .d  = d
    };
    MemoryContextSwitchTo(prev);
    return true;
}

static inline void
laplace_prompt_geometry_shape(LaplacePromptIntent *intent, uint32 root_node,
                              int fanout, const hash128_t *candidates, int candidate_count)
{
    if (candidate_count <= 0 || fanout <= 0)
        return;
    size_t node_count = tier_tree_node_count(intent->input->tree);
    const uint32 *first_children =
        tier_tree_first_child_idx_array(intent->input->tree);
    const uint32 *child_counts =
        tier_tree_child_count_array(intent->input->tree);
    const double *coords = tier_tree_coord_array(intent->input->tree);
    if ((size_t) root_node >= node_count)
        elog(ERROR, "prompt geometry: root curve node is outside canonical tree");
    uint32 first_child = first_children[root_node];
    uint32 child_count = child_counts[root_node];
    if (child_count < 2)
        return;
    if (first_child == TIER_TREE_INVALID ||
        (size_t) first_child + (size_t) child_count > node_count ||
        (Size) child_count > MaxAllocSize / sizeof(Datum))
        elog(ERROR, "prompt geometry: realized root curve is invalid");

    /* One point per immediate ordered constituent of the root: the level
     * public.laplace_curves realizes for each candidate. Points from another
     * composition level would not give a Frechet distance over one physicality. */
    ArrayType *cand_b3 = laplace_pub_blake3_array(candidates, candidate_count);
    Oid        b3_arr_oid = laplace_pub_blake3_array_oid();
    Datum      args[1] = { PointerGetDatum(cand_b3) };
    bool       nulls[1] = { false };
    PromptFrechetCtx fctx = {
        intent->owner,
        &coords[(Size) first_child * 4],
        (int) child_count,
        NULL, 0, 0
    };

    (void) laplace_pub_call_srf("public", "laplace_curves", 1, &b3_arr_oid,
                                args, nulls, visit_prompt_frechet_curve, &fctx);

    if (fctx.count > 1)
        qsort(fctx.items, (Size) fctx.count, sizeof(*fctx.items), laplace_prompt_frechet_compare);
    for (int row = 0; row < Min(fctx.count, fanout); ++row)
        laplace_prompt_geometry_append(
            intent, &intent->root, &fctx.items[row].id, LAPLACE_PROMPT_GEOMETRY_FRECHET,
            (uint32) row + 1u, NULL, fctx.items[row].d);

    if (fctx.items) pfree(fctx.items);
    pfree(cand_b3);
}

static inline void
laplace_prompt_geometry_couple(LaplacePromptIntent *intent, int fanout)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    intent->geometry_frontier = construct_empty_array(BYTEAOID);
    if (fanout <= 0)
    {
        MemoryContextSwitchTo(previous);
        return;
    }

    size_t tree_nodes = tier_tree_node_count(intent->input->tree);
    const hash128_t *tree_ids = tier_tree_id_array(intent->input->tree);
    uint32 root_node = TIER_TREE_INVALID;
    for (size_t node = 0; node < tree_nodes; ++node)
        if (hash128_eq(&tree_ids[node], &intent->root))
        {
            root_node = (uint32) node;
            break;
        }
    if (root_node == TIER_TREE_INVALID)
        elog(ERROR, "prompt geometry: admitted root is absent from canonical tree");

    /* The root carries every current occurrence as provenance, so geometry
     * reads once per metric plane from the root, not once per occurrence. */
    if ((Size) fanout > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "prompt geometry: fanout exceeds allocation capacity");
    hash128_t *root_angular = palloc(sizeof(hash128_t) * (Size) fanout);
    int root_angular_count = 0;
    laplace_prompt_geometry_scan_anchor(
        intent, &intent->root, root_node, fanout,
        root_angular, &root_angular_count);

    /* Frechet refinement ranks only the root's angular KNN population, over
     * each candidate's realized curve rather than its packed trajectory values. */
    laplace_prompt_geometry_shape(
        intent, root_node, fanout,
        root_angular, root_angular_count);
    pfree(root_angular);

    if (intent->geometry_count > 0)
    {
        HASHCTL seen_ctl = {0};
        seen_ctl.keysize = sizeof(hash128_t);
        seen_ctl.entrysize = sizeof(hash128_t);
        seen_ctl.hcxt = intent->owner;
        HTAB *frontier_seen = hash_create(
            "prompt geometry frontier identities", Max(intent->geometry_count, 16),
            &seen_ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        ArrayBuildState *frontier = NULL;
        for (int i = 0; i < intent->geometry_count; ++i)
        {
            const LaplacePromptGeometryCandidate *candidate = &intent->geometry[i];
            bool frontier_found;
            hash_search(frontier_seen, &candidate->id, HASH_ENTER, &frontier_found);
            if (!frontier_found)
            {
                Datum id = hash128_to_datum(&candidate->id);
                frontier = accumArrayResult(
                    frontier, id, false, BYTEAOID, intent->owner);
                pfree(DatumGetPointer(id));
            }
        }
        hash_destroy(frontier_seen);
        if (frontier)
        {
            pfree(intent->geometry_frontier);
            intent->geometry_frontier =
                DatumGetArrayTypeP(makeArrayResult(frontier, intent->owner));
        }
    }
    MemoryContextSwitchTo(previous);
}

#endif
