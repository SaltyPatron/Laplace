/* astar_path.c — the compiled cascade SRF (2026-06-06).
 *
 * laplace_astar_path: least-cost path over the consensus graph, the
 * substrate-native operator that replaces recursive-SQL / app-loop traversal.
 * The SEARCH is the C/C++ kernel (engine/core/astar.cpp — frontier heap,
 * visited/stale-skip, came-from reconstruction, max_depth, goal region); THIS
 * file is the thin PG boundary: it supplies the kernel its graph through an SPI
 * neighbor callback (per-node, ONE prepared plan per backend) and streams the
 * resolved path out as SRF rows.
 *
 * Edge cost is derived from Glicko μ — a stronger relation (higher μ) is a
 * cheaper hop — so least-cost = shortest, best-corroborated path. Refuted edges
 * (rating + 2·rd < neutral) are pruned in the neighbor query exactly as the SQL
 * reads do; a node over the cap keeps its top-μ neighbors (cheapest hops).
 *
 * Layer law: SQL/C orchestrate, the kernel does the heavy lifting. The neighbor
 * SET (which edges exist) is set-logic → SPI; the SEARCH (which path wins) is
 * algorithm → the compiled kernel.
 */

#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "laplace/core/astar.h"
#include "laplace/core/hash128.h"

PG_FUNCTION_INFO_V1(pg_laplace_astar_path);

/* Glicko neutral μ at scale 1e9 (1500.0). NOT-refuted ≡ rating + 2·rd ≥ neutral. */
#define LAPLACE_NEUTRAL_MU 1500000000000LL

/* Undirected neighbor set: edges out of (subject=$1) and into (object=$1) the
 * node, over the requested relation types ($2), non-refuted, top-μ-capped ($3).
 * Relatedness is symmetric, so a cascade walks edges either way; the directed
 * variant keeps only the forward (subject→object) orientation. */
static const char *Q_UNDIRECTED =
    "SELECT nbr, rating, rd FROM ("
    "  SELECT object_id AS nbr, rating, rd FROM laplace.consensus"
    "   WHERE subject_id = $1 AND object_id IS NOT NULL"
    "     AND type_id = ANY ($2) AND (rating + 2*rd) >= 1500000000000"
    "  UNION ALL"
    "  SELECT subject_id, rating, rd FROM laplace.consensus"
    "   WHERE object_id = $1"
    "     AND type_id = ANY ($2) AND (rating + 2*rd) >= 1500000000000"
    ") e ORDER BY (rating - 2*rd) DESC LIMIT $3";

static const char *Q_DIRECTED =
    "SELECT object_id AS nbr, rating, rd FROM laplace.consensus"
    "  WHERE subject_id = $1 AND object_id IS NOT NULL"
    "    AND type_id = ANY ($2) AND (rating + 2*rd) >= 1500000000000"
    "  ORDER BY (rating - 2*rd) DESC LIMIT $3";

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

/* μ → edge cost: base hop 1.0 + a sub-1.0 penalty that grows as μ falls below
 * neutral, so cost ∈ [1.0, 1.99). The penalty never reorders hop count
 * (shortest path always wins; μ only breaks ties among equal-length paths —
 * matching the validated SQL reason() behaviour). */
static double
edge_cost(int64 rating, int64 rd)
{
    double eff = (double) (rating - 2 * rd) / 1e9;   /* eff_mu_display */
    double pen = (1500.0 - eff) / 1000.0;
    if (pen < 0.0)  pen = 0.0;
    if (pen > 0.99) pen = 0.99;
    return 1.0 + pen;
}

/* Per-call neighbor provider for the kernel. ctx carries the relation-type
 * filter and a reusable single-row bytea for $1 (the node), so the hot loop
 * doesn't palloc a fresh node bytea every expansion. */
typedef struct {
    Datum  types;       /* bytea[] of relation type ids ($2) */
    bool   directed;
    bytea *nodebuf;     /* VARHDRSZ + 16, reused each call for $1 */
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
                          /*read_only*/ true, cap);
    if (rc != SPI_OK_SELECT)
        return -1;   /* kernel turns this into an empty (no-path) result */

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

static bytea *
hash_to_bytea(const hash128_t *h)
{
    bytea *b = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(b, VARHDRSZ + sizeof(hash128_t));
    memcpy(VARDATA(b), h, sizeof(hash128_t));
    return b;
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

    SPI_finish();   /* search complete; the path is held in the kernel handle */

    if (q != NULL)
    {
        while (astar_next(q, &step))
        {
            Datum values[3];
            bool  nulls[3] = { false, false, false };
            values[0] = Int32GetDatum(idx++);
            values[1] = PointerGetDatum(hash_to_bytea(&step.entity));
            values[2] = Float8GetDatum(step.g);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
        }
        astar_close(q);
    }

    return (Datum) 0;
}
