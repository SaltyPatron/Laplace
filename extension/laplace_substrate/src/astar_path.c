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
    "SELECT nbr, rating, rd FROM consensus.neighbors_undirected($1, $2, $3)";

static const char *Q_DIRECTED =
    "SELECT nbr, rating, rd FROM consensus.neighbors_directed($1, $2, $3)";

/* Single-key coordinate lookup, same ensure_*_plan cached-plan idiom as
 * containers_of.c and generate_walk.c's ordinal-continuity probe. Used only
 * when p_use_geometry is requested -- fetches one entity's own S3 point for
 * the admissible-heuristic closure below. */
static const char *Q_COORD =
    "SELECT ST_X(coord), ST_Y(coord), ST_Z(coord), ST_M(coord) "
    "FROM laplace.v_word_points WHERE id = $1 AND coord IS NOT NULL LIMIT 1";

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

static double
edge_cost(int64 rating, int64 rd)
{
    double probability = laplace_edge_strength(rating, rd);

    /* Independent-edge path likelihoods multiply; negative log turns that
     * product into the non-negative additive cost Dijkstra requires. A zero
     * expected score is not a traversable edge, handled by spi_expand. */
    return -log(probability);
}

typedef struct {
    Datum         types;
    bool          directed;
    bytea        *nodebuf;
    astar_edge_t *edges;
    Size          edge_capacity;
} expand_ctx;

static bool
spi_expand(void *ctxp, const hash128_t *node,
           const astar_edge_t **out, size_t *count)
{
    expand_ctx *ctx = (expand_ctx *) ctxp;
    Datum       args[3];
    char        nulls[4] = "  n";
    int         rc;
    uint64      r;
    size_t      n = 0;

    memcpy(VARDATA(ctx->nodebuf), node, sizeof(hash128_t));
    args[0] = PointerGetDatum(ctx->nodebuf);
    args[1] = ctx->types;
    args[2] = (Datum) 0; /* NULL p_limit means the complete adjacency list. */

    rc = SPI_execute_plan(ensure_plan(ctx->directed), args, nulls, true, 0);
    if (rc != SPI_OK_SELECT)
        return false;

    if (SPI_processed > (uint64) (MaxAllocSize / sizeof(astar_edge_t)))
        ereport(ERROR,
                (errmsg("astar_path: adjacency exceeds PostgreSQL allocation capacity"),
                 errdetail("Node expansion returned %llu edges.",
                           (unsigned long long) SPI_processed)));
    if ((Size) SPI_processed > ctx->edge_capacity)
    {
        Size bytes = Max((Size) 1, (Size) SPI_processed) * sizeof(astar_edge_t);
        ctx->edges = ctx->edges == NULL
                     ? (astar_edge_t *) palloc(bytes)
                     : (astar_edge_t *) repalloc(ctx->edges, bytes);
        ctx->edge_capacity = (Size) SPI_processed;
    }

    for (r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool      isnull;
        Datum     nbr = SPI_getbinval(tup, td, 1, &isnull);
        int64     rating, rd;
        double    probability;

        if (isnull) continue;
        rating    = DatumGetInt64(SPI_getbinval(tup, td, 2, &isnull));
        rd        = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
        probability = laplace_edge_strength(rating, rd);
        if (probability <= 0.0)
            continue;

        ctx->edges[n].target = *(hash128_t *) VARDATA_ANY(DatumGetByteaPP(nbr));
        ctx->edges[n].cost   = edge_cost(rating, rd);
        n++;
    }
    /* All row data copied into out[] above; free before the next probe. */
    SPI_freetuptable(SPI_tuptable);
    *out = ctx->edges;
    *count = n;
    return true;
}

/* p_use_geometry tie-order closure: goal coordinates are resolved once up
 * front and each call returns normalized angular distance. The core orders
 * primarily by exact accumulated cost and consults this only for equal-cost
 * ties. No relationship between S3 distance and consensus probability has
 * been proved, so geometry must not masquerade as an admissible cost bound.
 * A node or goal with no point physicality degrades to 0.0, never errors.
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
    max_depth    = PG_ARGISNULL(3) ? PG_INT32_MAX : PG_GETARG_INT32(3);
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
    ctx.edges         = NULL;
    ctx.edge_capacity = 0;

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
