#include "postgres.h"

#include <math.h>

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "laplace/core/astar.h"
#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "laplace/core/math4d.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_astar_path);

/* Same local constant as generate_walk.c -- avoids relying on M_PI, which
 * isn't portably defined under MSVC without _USE_MATH_DEFINES. */
#define ASTAR_PI 3.14159265358979323846

static const char *Q_UNDIRECTED =
    "SELECT nbr, rating, rd, witness_count FROM laplace.consensus_neighbors_undirected($1, $2, $3)";

static const char *Q_DIRECTED =
    "SELECT nbr, rating, rd, witness_count FROM laplace.consensus_neighbors_directed($1, $2, $3)";

/* Single-key coordinate lookup, same ensure_*_plan cached-plan idiom as
 * containers_of.c and generate_walk.c's ordinal-continuity probe. Used only
 * when p_use_geometry is requested -- fetches one entity's own S3 point for
 * the admissible-heuristic closure below. */
static const char *Q_COORD =
    "SELECT ST_X(coord), ST_Y(coord), ST_Z(coord), ST_M(coord) "
    "FROM laplace.physicalities WHERE entity_id = $1 AND type = 1 LIMIT 1";

static SPIPlanPtr coord_plan = NULL;

static void
ensure_coord_plan(void)
{
    if (coord_plan == NULL)
    {
        Oid argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(Q_COORD, 1, argtypes);
        if (plan == NULL)
            elog(ERROR, "astar_path: SPI_prepare(coord) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "astar_path: SPI_keepplan(coord) failed");
        coord_plan = plan;
    }
}

/* Returns false if no point physicality is on file for this entity -- the
 * heuristic must degrade to 0.0 (still admissible), never error. `scratch`
 * is a caller-owned, reusable VARHDRSZ+sizeof(hash128_t) buffer (same
 * pattern as expand_ctx.nodebuf below) -- avoids a palloc per lookup. */
static bool
fetch_coord(const hash128_t *id, bytea *scratch, double out_xyzm[4])
{
    Datum args[1];
    int   rc;
    bool  isnull, cnull;

    memcpy(VARDATA(scratch), id, sizeof(hash128_t));
    args[0] = PointerGetDatum(scratch);

    ensure_coord_plan();
    rc = SPI_execute_plan(coord_plan, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return false;
    out_xyzm[0] = DatumGetFloat8(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
    out_xyzm[1] = DatumGetFloat8(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2, &cnull)); isnull |= cnull;
    out_xyzm[2] = DatumGetFloat8(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 3, &cnull)); isnull |= cnull;
    out_xyzm[3] = DatumGetFloat8(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 4, &cnull)); isnull |= cnull;
    return !isnull;
}

static SPIPlanPtr plan_undirected = NULL;
static SPIPlanPtr plan_directed   = NULL;

static SPIPlanPtr
ensure_plan(bool directed)
{
    SPIPlanPtr *slot = directed ? &plan_directed : &plan_undirected;
    if (*slot == NULL)
    {
        Oid        argtypes[3] = { BYTEAOID, BYTEAARRAYOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare(directed ? Q_DIRECTED : Q_UNDIRECTED, 3, argtypes);
        if (plan == NULL)
            elog(ERROR, "astar_path: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "astar_path: SPI_keepplan failed");
        *slot = plan;
    }
    return *slot;
}

/*
 * Rule #1 reuse: the SAME laplace_walk_edge_weight formula generate_walk.c's
 * beam scorer uses (doc 14 P5 / doc 15 3Ca), squashed from an unbounded
 * signed weight into astar's [1.0, ~1.99) cost domain via a logistic curve
 * so higher confidence/witnessing/lower-rd still means cheaper traversal,
 * same direction the original bare (neutral-eff)/1000 penalty had. SCALE is
 * an initial, empirically-untuned sensitivity constant (Rule #5 caveat:
 * profile before retuning against live data), not a derived quantity.
 */
#define EDGE_COST_LOGISTIC_SCALE 100.0

static double
edge_cost(int64 rating, int64 rd, int64 witness_count, double kappa)
{
    double weight = laplace_walk_edge_weight(rating, rd, witness_count, kappa);
    return 1.0 + 0.99 / (1.0 + exp(weight / EDGE_COST_LOGISTIC_SCALE));
}

typedef struct {
    Datum  types;
    bool   directed;
    bytea *nodebuf;
    double kappa;
} expand_ctx;

static int
spi_expand(void *ctxp, const hash128_t *node, astar_edge_t *out, int cap)
{
    expand_ctx *ctx = (expand_ctx *) ctxp;
    Datum       args[3];
    int         rc;
    uint64      r;

    memcpy(VARDATA(ctx->nodebuf), node, sizeof(hash128_t));
    args[0] = PointerGetDatum(ctx->nodebuf);
    args[1] = ctx->types;
    args[2] = Int32GetDatum(cap);

    rc = SPI_execute_plan(ensure_plan(ctx->directed), args, NULL,
                          true, cap);
    if (rc != SPI_OK_SELECT)
        return -1;

    for (r = 0; r < SPI_processed && (int) r < cap; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool      isnull;
        Datum     nbr = SPI_getbinval(tup, td, 1, &isnull);
        int64     rating, rd, witnesses;

        if (isnull) continue;
        rating    = DatumGetInt64(SPI_getbinval(tup, td, 2, &isnull));
        rd        = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
        witnesses = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));

        out[r].target = *(hash128_t *) VARDATA_ANY(DatumGetByteaPP(nbr));
        out[r].cost   = edge_cost(rating, rd, witnesses, ctx->kappa);
    }
    /* All row data copied into out[] above; free before the next probe. */
    SPI_freetuptable(SPI_tuptable);
    return (int) r;
}

/* p_use_geometry heuristic closure: goal coordinates are resolved once up
 * front (goal_xyzm/goal_ok, sized goal_n); each call fetches the CURRENT
 * node's own coordinate (fetch_coord, cached plan) and returns the minimum
 * angular distance to any resolved goal, normalized by pi. Since pi is the
 * maximum possible S3 angular distance, h = dist/pi is always <= 1.0, and
 * edge_cost's logistic squash always returns a cost strictly > 1.0 per hop
 * -- so h never overestimates the true cost of the >=1 remaining hop any
 * admissible path needs. This is a deliberately WEAK but honestly-provable
 * bound: no live calibration query is required (an empirically tighter
 * bound, e.g. from a measured max single-hop angular step, is a documented
 * future refinement -- see doc 15 3Cc / Issue 05 coordination note). A node
 * or goal with no point physicality on file degrades to h=0.0, never errors.
 */
typedef struct {
    double *goal_xyzm; /* goal_n * 4 doubles */
    bool   *goal_ok;
    size_t  goal_n;
    bytea  *scratch;
} heuristic_ctx;

static double
astar_geo_heuristic(void *ctxp, const hash128_t *node,
                    const hash128_t *goal_region, size_t goal_count)
{
    heuristic_ctx *ctx = (heuristic_ctx *) ctxp;
    double node_xyzm[4];
    double best = -1.0;

    (void) goal_region; (void) goal_count; /* precomputed into ctx->goal_xyzm instead */

    if (!fetch_coord(node, ctx->scratch, node_xyzm))
        return 0.0;

    for (size_t i = 0; i < ctx->goal_n; i++)
    {
        double dist;
        if (!ctx->goal_ok[i])
            continue;
        dist = math4d_angular_distance(node_xyzm, &ctx->goal_xyzm[i * 4]);
        if (best < 0.0 || dist < best)
            best = dist;
    }
    if (best < 0.0)
        return 0.0;
    return best / ASTAR_PI;
}

Datum
pg_laplace_astar_path(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea         *start_b;
    ArrayType     *goals_arr;
    Datum         *goal_elems;
    bool          *goal_nulls;
    int            goal_n;
    int32          max_depth;
    bool           directed;
    bool           use_geometry;
    hash128_t      start_h;
    hash128_t     *goal_h;
    expand_ctx     ctx;
    heuristic_ctx  hctx;
    astar_query_t *q;
    astar_step_t   step;
    int            idx = 0;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("astar_path: start and goals must not be NULL")));
    start_b      = PG_GETARG_BYTEA_PP(0);
    goals_arr    = PG_GETARG_ARRAYTYPE_P(1);
    max_depth    = PG_ARGISNULL(3) ? 7 : PG_GETARG_INT32(3);
    directed     = PG_ARGISNULL(4) ? false : PG_GETARG_BOOL(4);
    use_geometry = (PG_NARGS() > 5 && !PG_ARGISNULL(5)) ? PG_GETARG_BOOL(5) : false;
    if (PG_ARGISNULL(2))
        ereport(ERROR, (errmsg("astar_path: relation types ($3) must not be NULL")));
    if (max_depth < 0)
        ereport(ERROR, (errmsg("astar_path: max_depth must be >= 0")));

    start_h = *(hash128_t *) VARDATA_ANY(start_b);

    deconstruct_array(goals_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &goal_elems, &goal_nulls, &goal_n);
    if (goal_n == 0)
        ereport(ERROR, (errmsg("astar_path: goal region must be non-empty")));
    goal_h = (hash128_t *) palloc(sizeof(hash128_t) * goal_n);
    for (int i = 0; i < goal_n; i++)
    {
        if (goal_nulls[i])
            ereport(ERROR, (errmsg("astar_path: goal region must not contain NULL")));
        goal_h[i] = *(hash128_t *) VARDATA_ANY(DatumGetByteaPP(goal_elems[i]));
    }

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "astar_path: SPI_connect failed");

    ctx.types    = PG_GETARG_DATUM(2);
    ctx.directed = directed;
    ctx.nodebuf  = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(ctx.nodebuf, VARHDRSZ + sizeof(hash128_t));
    ctx.kappa    = spi_fetch_rd_kappa();

    if (use_geometry)
    {
        hctx.goal_n    = (size_t) goal_n;
        hctx.goal_xyzm = (double *) palloc(sizeof(double) * 4 * (size_t) goal_n);
        hctx.goal_ok   = (bool *) palloc(sizeof(bool) * (size_t) goal_n);
        hctx.scratch   = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
        SET_VARSIZE(hctx.scratch, VARHDRSZ + sizeof(hash128_t));
        for (int i = 0; i < goal_n; i++)
            hctx.goal_ok[i] = fetch_coord(&goal_h[i], hctx.scratch, &hctx.goal_xyzm[i * 4]);
    }

    q = astar_open(&start_h, goal_h, (size_t) goal_n,
                   (size_t) max_depth, 1, spi_expand, &ctx,
                   use_geometry ? astar_geo_heuristic : NULL,
                   use_geometry ? &hctx : NULL);

    SPI_finish();

    if (q != NULL)
    {
        while (astar_next(q, &step))
        {
            Datum values[3];
            bool  nulls[3] = { false, false, false };
            values[0] = Int32GetDatum(idx++);
            values[1] = hash128_to_datum(&step.entity);
            values[2] = Float8GetDatum(step.g);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
        astar_close(q);
    }

    return (Datum) 0;
}
