/*
 * point4d_gist.c — GiST support functions for POINT4D.
 *
 * Enables spatial KNN queries on POINT4D columns:
 *   CREATE INDEX ... USING gist (some_point4d_column);
 *   SELECT * FROM t ORDER BY p <-> $query LIMIT $k;  -- index-backed
 *
 * Storage strategy: BOX4D. Each leaf POINT4D is wrapped as a degenerate
 * box (min == max == p); internal nodes store the union of children's
 * bounding boxes. Standard "box-of-points" GiST pattern (cf. btree_gist
 * for box, PostGIS gist_geometry_ops_nd).
 *
 * Operator class registered in SQL: point4d_gist_ops with strategies
 *   1  = (point4d, point4d)             — exact equality (with recheck)
 *   15 <-> (point4d, point4d) ORDER BY  — KNN distance ordering
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "access/gist.h"
#include "access/skey.h"
#include "access/stratnum.h"

#include "laplace_pg/point4d_type.h"
#include "laplace_pg/box4d_type.h"
#include "laplace_pg/box4d_ops.h"

#define LAPLACE_POINT4D_GIST_STRATEGY_EQ      1
#define LAPLACE_POINT4D_GIST_STRATEGY_KNN     15

/* ------------------------------------------------------------------ */
/* GiST support functions — registered in SQL via point4d_gist_ops.   */
/* ------------------------------------------------------------------ */

/* Function 3 — compress: leaf POINT4D → BOX4D index key. */
PG_FUNCTION_INFO_V1(point4d_gist_compress);
Datum point4d_gist_compress(PG_FUNCTION_ARGS)
{
    GISTENTRY *entry  = (GISTENTRY *) PG_GETARG_POINTER(0);
    GISTENTRY *retval = entry;
    if (entry->leafkey)
    {
        laplace_point4d_pg_t *p   = DatumGetPoint4D(entry->key);
        laplace_box4d_pg_t   *box = (laplace_box4d_pg_t *) palloc(sizeof(*box));
        laplace_box4d_init_from_point(box, p);
        retval = (GISTENTRY *) palloc(sizeof(*retval));
        gistentryinit(*retval, Box4DGetDatum(box),
                      entry->rel, entry->page, entry->offset, false);
    }
    PG_RETURN_POINTER(retval);
}

/* Function 2 — union: combine N entries' BOX4D keys into one. */
PG_FUNCTION_INFO_V1(point4d_gist_union);
Datum point4d_gist_union(PG_FUNCTION_ARGS)
{
    GistEntryVector *entryvec = (GistEntryVector *) PG_GETARG_POINTER(0);
    int             *sizep    = (int *)             PG_GETARG_POINTER(1);

    laplace_box4d_pg_t *out = (laplace_box4d_pg_t *) palloc(sizeof(*out));
    *out = *DatumGetBox4D(entryvec->vector[0].key);
    for (int i = 1; i < entryvec->n; i++) {
        laplace_box4d_pg_t *b = DatumGetBox4D(entryvec->vector[i].key);
        laplace_box4d_union(out, out, b);
    }
    *sizep = sizeof(laplace_box4d_pg_t);
    PG_RETURN_POINTER(out);
}

/* Function 1 — consistent: does this entry's bbox potentially contain
 * a row matching the query? Recheck always set so leaf-level comparison
 * resolves exact match. */
PG_FUNCTION_INFO_V1(point4d_gist_consistent);
Datum point4d_gist_consistent(PG_FUNCTION_ARGS)
{
    GISTENTRY            *entry    = (GISTENTRY *) PG_GETARG_POINTER(0);
    laplace_point4d_pg_t *query    = PG_GETARG_POINT4D(1);
    StrategyNumber        strategy = (StrategyNumber) PG_GETARG_UINT16(2);
    /* Oid                subtype  = PG_GETARG_OID(3); */
    bool                 *recheck  = (bool *) PG_GETARG_POINTER(4);

    laplace_box4d_pg_t *key = DatumGetBox4D(entry->key);

    *recheck = true;

    switch (strategy)
    {
        case LAPLACE_POINT4D_GIST_STRATEGY_EQ:
            PG_RETURN_BOOL(laplace_box4d_contains_point(key, query));
        default:
            PG_RETURN_BOOL(false);
    }
}

/* Function 8 — distance: KNN lower-bound distance from query POINT4D
 * to entry's BOX4D. Internal nodes return min-distance to bounding box;
 * leaf nodes (degenerate box) return exact point-to-point distance. */
PG_FUNCTION_INFO_V1(point4d_gist_distance);
Datum point4d_gist_distance(PG_FUNCTION_ARGS)
{
    GISTENTRY            *entry = (GISTENTRY *) PG_GETARG_POINTER(0);
    laplace_point4d_pg_t *query = PG_GETARG_POINT4D(1);

    laplace_box4d_pg_t *key = DatumGetBox4D(entry->key);
    PG_RETURN_FLOAT8(laplace_box4d_min_distance_to_point(key, query));
}

/* Function 5 — penalty: cost of inserting `new` into `original`. Edge-sum
 * expansion (robust to degenerate boxes; volume would be 0 for points). */
PG_FUNCTION_INFO_V1(point4d_gist_penalty);
Datum point4d_gist_penalty(PG_FUNCTION_ARGS)
{
    GISTENTRY *origentry = (GISTENTRY *) PG_GETARG_POINTER(0);
    GISTENTRY *newentry  = (GISTENTRY *) PG_GETARG_POINTER(1);
    float     *penalty   = (float *)     PG_GETARG_POINTER(2);

    laplace_box4d_pg_t *orig = DatumGetBox4D(origentry->key);
    laplace_box4d_pg_t *new_ = DatumGetBox4D(newentry->key);

    laplace_box4d_pg_t merged;
    laplace_box4d_union(&merged, orig, new_);
    *penalty = (float) (laplace_box4d_size(&merged) - laplace_box4d_size(orig));
    PG_RETURN_POINTER(penalty);
}

/* Function 7 — same: byte-equality of two BOX4D keys (used in update path). */
PG_FUNCTION_INFO_V1(point4d_gist_same);
Datum point4d_gist_same(PG_FUNCTION_ARGS)
{
    laplace_box4d_pg_t *a      = PG_GETARG_BOX4D(0);
    laplace_box4d_pg_t *b      = PG_GETARG_BOX4D(1);
    bool               *result = (bool *) PG_GETARG_POINTER(2);
    *result = a->min_x == b->min_x && a->max_x == b->max_x
           && a->min_y == b->min_y && a->max_y == b->max_y
           && a->min_z == b->min_z && a->max_z == b->max_z
           && a->min_w == b->min_w && a->max_w == b->max_w;
    PG_RETURN_POINTER(result);
}

/* Function 6 — picksplit: Guttman quadratic split. Pick two seeds with
 * the largest pairwise expansion cost; assign each remaining entry to
 * whichever group has lower penalty. */
PG_FUNCTION_INFO_V1(point4d_gist_picksplit);
Datum point4d_gist_picksplit(PG_FUNCTION_ARGS)
{
    GistEntryVector *entryvec = (GistEntryVector *) PG_GETARG_POINTER(0);
    GIST_SPLITVEC   *v        = (GIST_SPLITVEC *)   PG_GETARG_POINTER(1);
    int n = entryvec->n;  /* 1-based; valid entries at indices 1..n-1 */

    OffsetNumber *left  = (OffsetNumber *) palloc(sizeof(OffsetNumber) * (size_t) n);
    OffsetNumber *right = (OffsetNumber *) palloc(sizeof(OffsetNumber) * (size_t) n);
    int nleft = 0, nright = 0;

    /* Pick the two seeds with maximum pairwise wasted area when merged. */
    int seed_a = 1, seed_b = 2;
    double worst = -1.0;
    for (int i = 1; i < n; i++) {
        for (int j = i + 1; j < n; j++) {
            laplace_box4d_pg_t *bi = DatumGetBox4D(entryvec->vector[i].key);
            laplace_box4d_pg_t *bj = DatumGetBox4D(entryvec->vector[j].key);
            laplace_box4d_pg_t merged;
            laplace_box4d_union(&merged, bi, bj);
            double waste = laplace_box4d_size(&merged)
                         - laplace_box4d_size(bi)
                         - laplace_box4d_size(bj);
            if (waste > worst) { worst = waste; seed_a = i; seed_b = j; }
        }
    }

    laplace_box4d_pg_t *group_a = (laplace_box4d_pg_t *) palloc(sizeof(*group_a));
    laplace_box4d_pg_t *group_b = (laplace_box4d_pg_t *) palloc(sizeof(*group_b));
    *group_a = *DatumGetBox4D(entryvec->vector[seed_a].key);
    *group_b = *DatumGetBox4D(entryvec->vector[seed_b].key);
    left[nleft++]   = (OffsetNumber) seed_a;
    right[nright++] = (OffsetNumber) seed_b;

    for (int i = 1; i < n; i++) {
        if (i == seed_a || i == seed_b) continue;
        laplace_box4d_pg_t *bi = DatumGetBox4D(entryvec->vector[i].key);
        laplace_box4d_pg_t merged_a, merged_b;
        laplace_box4d_union(&merged_a, group_a, bi);
        laplace_box4d_union(&merged_b, group_b, bi);
        double pa = laplace_box4d_size(&merged_a) - laplace_box4d_size(group_a);
        double pb = laplace_box4d_size(&merged_b) - laplace_box4d_size(group_b);
        if (pa < pb) {
            *group_a = merged_a;
            left[nleft++] = (OffsetNumber) i;
        } else {
            *group_b = merged_b;
            right[nright++] = (OffsetNumber) i;
        }
    }

    v->spl_left   = left;
    v->spl_nleft  = nleft;
    v->spl_ldatum = Box4DGetDatum(group_a);
    v->spl_right  = right;
    v->spl_nright = nright;
    v->spl_rdatum = Box4DGetDatum(group_b);
    PG_RETURN_POINTER(v);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
