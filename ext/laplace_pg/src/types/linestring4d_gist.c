/*
 * linestring4d_gist.c — GiST support functions for LINESTRING4D.
 *
 * Tier-1+ substrate compositions are stored as LINESTRING4D values
 * (vertices = constituent positions). Indexing them with GiST enables
 * KNN ordering of trajectories by Fréchet distance to a query
 * trajectory — substrate's named primitive for shape-based similarity
 * (substrate-synthesis.md lines 78, 246, 289):
 *
 *   "Fréchet and Hausdorff distances apply at every tier for shape-
 *    based similarity ('Frayed edge detection' across error patterns,
 *    log structures, audio spectra, anything trajectory-shaped)."
 *
 *   "Multi-criteria ranking (geometric proximity + Glicko-2 rating
 *    profile + Fréchet shape similarity)"
 *
 * Storage strategy: BOX4D. Each leaf LINESTRING4D is wrapped as the
 * axis-aligned bounding box over its vertices; internal nodes store
 * the union of children's bounding boxes. The same BOX4D-keyed
 * union/same/penalty/picksplit functions used by point4d_gist_ops are
 * reused unchanged (they operate purely on BOX4D and are invariant to
 * leaf source type).
 *
 * Operator class registered in SQL: linestring4d_gist_ops with
 *   STRATEGY 15: <-> (linestring4d, linestring4d) FOR ORDER BY
 *
 * The <-> operator is bound to laplace.frechet_distance (already
 * implemented in linestring4d_ops_sql.c). The GiST distance support
 * function returns laplace_box4d_min_distance(query_bbox, entry_bbox)
 * as an admissible lower bound for Fréchet (proof: for any alignment
 * of trajectories whose vertices lie in the two boxes, every matched
 * pair has L2 distance >= bbox_min_distance, so the alignment's max
 * pair-distance — i.e. its Fréchet score — is >= bbox_min_distance).
 *
 * Trajectory-near-a-point queries do NOT belong on this opclass; they
 * belong on the centroid POINT4D column with point4d_gist_ops. Per
 * synthesis line 76, the substrate stores a tier-1+ composition's
 * representative position as the centroid POINT4D in the 4-ball; the
 * LINESTRING4D is the trajectory itself, queried by shape, not by
 * proximity-to-point.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "access/gist.h"
#include "access/skey.h"
#include "access/stratnum.h"

#include "laplace_pg/point4d_type.h"
#include "laplace_pg/linestring4d_type.h"
#include "laplace_pg/box4d_type.h"
#include "laplace_pg/box4d_ops.h"

#define LAPLACE_LS4D_GIST_STRATEGY_KNN  15

/* AABB over a LINESTRING4D's vertices. Same math as the
 * linestring4d_envelope SQL function, inlined here so the GiST
 * translation unit doesn't take a runtime call into the SQL-binding
 * TU during index probes. */
static void
laplace_ls4d_envelope(laplace_box4d_pg_t *out,
                      const laplace_linestring4d_pg_t *ls)
{
    if (ls->vertex_count == 0) {
        out->min_x = out->min_y = out->min_z = out->min_w = 0.0;
        out->max_x = out->max_y = out->max_z = out->max_w = 0.0;
        return;
    }
    out->min_x = out->max_x = ls->vertices[0];
    out->min_y = out->max_y = ls->vertices[1];
    out->min_z = out->max_z = ls->vertices[2];
    out->min_w = out->max_w = ls->vertices[3];
    for (uint32 i = 1; i < ls->vertex_count; ++i) {
        const double *v = &ls->vertices[i * 4];
        if (v[0] < out->min_x) out->min_x = v[0]; else if (v[0] > out->max_x) out->max_x = v[0];
        if (v[1] < out->min_y) out->min_y = v[1]; else if (v[1] > out->max_y) out->max_y = v[1];
        if (v[2] < out->min_z) out->min_z = v[2]; else if (v[2] > out->max_z) out->max_z = v[2];
        if (v[3] < out->min_w) out->min_w = v[3]; else if (v[3] > out->max_w) out->max_w = v[3];
    }
}

/* GiST FUNCTION 3 — compress: leaf LINESTRING4D → BOX4D index key. */
PG_FUNCTION_INFO_V1(linestring4d_gist_compress);
Datum linestring4d_gist_compress(PG_FUNCTION_ARGS)
{
    GISTENTRY *entry  = (GISTENTRY *) PG_GETARG_POINTER(0);
    GISTENTRY *retval = entry;
    if (entry->leafkey)
    {
        laplace_linestring4d_pg_t *ls  = DatumGetLineString4D(entry->key);
        laplace_box4d_pg_t        *box = (laplace_box4d_pg_t *) palloc(sizeof(*box));
        laplace_ls4d_envelope(box, ls);
        retval = (GISTENTRY *) palloc(sizeof(*retval));
        gistentryinit(*retval, Box4DGetDatum(box),
                      entry->rel, entry->page, entry->offset, false);
    }
    PG_RETURN_POINTER(retval);
}

/* GiST FUNCTION 1 — consistent: required by GiST AM but only consulted
 * for filter strategies. The opclass declares only KNN (strategy 15),
 * which goes through FUNCTION 8 (distance) instead. Defaults to false
 * with recheck=true so any future filter strategy added without
 * updating this branch fails closed (visible empty result) rather than
 * silently mis-prunes. */
PG_FUNCTION_INFO_V1(linestring4d_gist_consistent);
Datum linestring4d_gist_consistent(PG_FUNCTION_ARGS)
{
    StrategyNumber  strategy = (StrategyNumber) PG_GETARG_UINT16(2);
    bool           *recheck  = (bool *) PG_GETARG_POINTER(4);
    *recheck = true;
    (void) strategy;
    PG_RETURN_BOOL(false);
}

/* GiST FUNCTION 8 — distance: KNN lower bound for Fréchet ordering.
 * Computes the query trajectory's AABB and returns
 * laplace_box4d_min_distance(query_bbox, entry_bbox). Admissible per
 * the file-header proof: every alignment between two trajectories must
 * have at least one matched pair with distance >= bbox_min_distance,
 * so the alignment's max pair-distance (its Fréchet score) is bounded
 * below by this value. The exact Fréchet is computed by Postgres when
 * it rechecks the <-> operator at the row level. */
PG_FUNCTION_INFO_V1(linestring4d_gist_distance);
Datum linestring4d_gist_distance(PG_FUNCTION_ARGS)
{
    GISTENTRY                 *entry = (GISTENTRY *) PG_GETARG_POINTER(0);
    laplace_linestring4d_pg_t *query = PG_GETARG_LINESTRING4D(1);

    laplace_box4d_pg_t *key = DatumGetBox4D(entry->key);
    laplace_box4d_pg_t  query_box;
    laplace_ls4d_envelope(&query_box, query);

    PG_RETURN_FLOAT8(laplace_box4d_min_distance(key, &query_box));
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
