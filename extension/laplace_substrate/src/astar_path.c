#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "laplace/core/astar.h"
#include "laplace/core/hash128.h"
#include "laplace/core/glicko2.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_astar_path);





static const char *Q_UNDIRECTED =
    "SELECT nbr, rating, rd FROM laplace.consensus_neighbors_undirected($1, $2, $3)";

static const char *Q_DIRECTED =
    "SELECT nbr, rating, rd FROM laplace.consensus_neighbors_directed($1, $2, $3)";

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
    double eff     = (double) laplace_effective_mu_fp(rating, rd) / 1e9;
    double neutral = (double) laplace_glicko2_neutral_mu_fp() / 1e9;
    double pen     = (neutral - eff) / 1000.0;
    if (pen < 0.0)  pen = 0.0;
    if (pen > 0.99) pen = 0.99;
    return 1.0 + pen;
}

typedef struct {
    Datum  types;
    bool   directed;
    bytea *nodebuf;
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
        int64     rating, rd;

        if (isnull) continue;
        rating = DatumGetInt64(SPI_getbinval(tup, td, 2, &isnull));
        rd     = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));

        out[r].target = *(hash128_t *) VARDATA_ANY(DatumGetByteaPP(nbr));
        out[r].cost   = edge_cost(rating, rd);
    }
    return (int) r;
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
    hash128_t      start_h;
    hash128_t     *goal_h;
    expand_ctx     ctx;
    astar_query_t *q;
    astar_step_t   step;
    int            idx = 0;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("astar_path: start and goals must not be NULL")));
    start_b   = PG_GETARG_BYTEA_PP(0);
    goals_arr = PG_GETARG_ARRAYTYPE_P(1);
    max_depth = PG_ARGISNULL(3) ? 7 : PG_GETARG_INT32(3);
    directed  = PG_ARGISNULL(4) ? false : PG_GETARG_BOOL(4);
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

    q = astar_open(&start_h, goal_h, (size_t) goal_n,
                   (size_t) max_depth, 1, spi_expand, &ctx);

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
