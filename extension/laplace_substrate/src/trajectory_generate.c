/*
 * trajectory_generate.c — walk_continuations: the S6→S7→S8 emission loop
 * (docs/specs/36 §3), corpus-free.
 *
 * S6 PROPOSE  trajectory_continuations($tail, $topk): the k-context successor
 *             read straight off physicalities.trajectory (GIN containment +
 *             ordinal window), with trigram→…→unigram backoff over max_stride.
 *             The trajectory IS the ordered sequence (§9); the per-backend
 *             GenCorpus rebuild of it — 97,111,658 rows streamed into a RAM
 *             suffix array on FIRST CALL of every connection, measured 46
 *             minutes before generate('The king', 40) could emit a token — is
 *             deleted, not optimized. §7 requires cost bounded by the path,
 *             not the corpus, and a whole-corpus build cannot satisfy that at
 *             any size.
 *
 * S7 STEER    steer_candidates($cands, $frontier): re-rank by rated consensus
 *             mass reaching the LIVE frontier — here the prompt's own content
 *             ids, re-scored per emitted token. Scored by walk_score.h, the
 *             same kernel walk_branches retrieves with, so proposing and
 *             steering cannot disagree about what an edge is worth.
 *
 *             The combination is signed and multiplicative, matching the
 *             walk's existing semantics (rank × edge_weight precedent, and
 *             walk_branches' "non-positive score must dead-end, not walk"):
 *               edges > 0, steer > 0  → sequence weight × steer
 *               edges = 0             → sequence weight × 1  (UNATTESTED is
 *                                       not refuted — the bool_or/NULL
 *                                       distinction, kept on purpose)
 *               edges > 0, steer ≤ 0 → excluded (adjudicated against the
 *                                       frontier: refuted edges dead-end)
 *
 * S8 SAMPLE   Gumbel draw over the top-k surviving candidates at the caller's
 *             spread. (Spec S8 names RD-as-temperature; RD already shapes the
 *             steer term through exp(−κ·rd) inside walk_edge_weight, so the
 *             caller's spread composes with it rather than replacing it.
 *             Making RD the SOLE temperature is a candidate follow-up, not
 *             smuggled in here.)
 *
 * FLOOR       walk_completes_floor (consensus COMPLETES_TO) when the sequence
 *             well is dry — unchanged from the corpus era; it was always a
 *             substrate read.
 *
 * All ids stay bytea end to end. The vocab intern table died with the corpus:
 * interning existed to map ids into the suffix array's int32 space, and there
 * is no suffix array.
 */
#include "postgres.h"

#include <math.h>

#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"
#include "common/pg_prng.h"

#include "laplace/core/hash128.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);

#define GEN_MAX_STEPS  4096
#define GEN_MAX_ORDER  8
#define GEN_CAND_CAP   256

typedef struct Cand
{
    Datum  obj;        /* bytea(16), caller-context copy */
    Datum  sep;        /* bytea(16) or (Datum) 0         */
    int64  weight;     /* S6 sequence count              */
    double steer;      /* S7 signed consensus mass       */
    int64  edges;      /* S7 edge count; 0 = unattested  */
    double eff;        /* combined sampling weight       */
} Cand;

static SPIPlanPtr propose_plan = NULL;
static SPIPlanPtr steer_plan   = NULL;
static SPIPlanPtr floor_plan   = NULL;

/*
 * Prepared once per backend and kept: the un-prepared path re-plans on every
 * emitted token of every walk. The LIMIT lives in the query text as a bound
 * parameter, never as SPI_execute_plan's count — the count stops the fetch
 * after the bitmap is already built (#691).
 */
static void
ensure_plans(void)
{
    if (propose_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT object_id, sep_id, weight "
            "FROM generation.trajectory_continuations($1, $2)",
            2, argtypes);

        if (plan == NULL)
            elog(ERROR, "walk_continuations: SPI_prepare(propose) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_continuations: SPI_keepplan(propose) failed");
        propose_plan = plan;
    }
    if (steer_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT candidate, steer, edges "
            "FROM generation.steer_candidates($1, $2)",
            2, argtypes);

        if (plan == NULL)
            elog(ERROR, "walk_continuations: SPI_prepare(steer) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_continuations: SPI_keepplan(steer) failed");
        steer_plan = plan;
    }
    if (floor_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT object_id, weight "
            "FROM generation.walk_completes_floor($1, $2)",
            2, argtypes);

        if (plan == NULL)
            elog(ERROR, "walk_continuations: SPI_prepare(floor) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_continuations: SPI_keepplan(floor) failed");
        floor_plan = plan;
    }
}

static uint64
splitmix64(uint64 *state)
{
    uint64 z = (*state += UINT64CONST(0x9E3779B97F4A7C15));
    z = (z ^ (z >> 30)) * UINT64CONST(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)) * UINT64CONST(0x94D049BB133111EB);
    return z ^ (z >> 31);
}

static double
rng_uniform(uint64 *state)
{
    return ((double) (splitmix64(state) >> 11) + 0.5) * (1.0 / 9007199254740992.0);
}

/* Copy a 16-byte bytea datum out of SPI_tuptable into the caller's context. */
static Datum
copy_id_datum(Datum d)
{
    bytea *src = DatumGetByteaPP(d);
    bytea *dst = (bytea *) palloc(VARHDRSZ + 16);

    SET_VARSIZE(dst, VARHDRSZ + 16);
    memcpy(VARDATA(dst), VARDATA_ANY(src), 16);
    return PointerGetDatum(dst);
}

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *ctx_arr;
    int32      steps, max_order, topk;
    float8     temp;
    uint64     rng;
    Datum     *elems;
    bool      *nulls;
    int        n_in;
    Datum     *ctx;
    int        ctx_len = 0, ctx_cap;
    Datum     *frontier;
    int        n_frontier = 0;
    Cand      *cand;
    MemoryContext walk_cxt, old;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("walk_continuations: context must not be NULL")));
    ctx_arr   = PG_GETARG_ARRAYTYPE_P(0);
    steps     = PG_ARGISNULL(1) ? 24  : PG_GETARG_INT32(1);
    max_order = PG_ARGISNULL(2) ? 5   : PG_GETARG_INT32(2);
    temp      = PG_ARGISNULL(3) ? 0.7 : PG_GETARG_FLOAT8(3);
    topk      = PG_ARGISNULL(4) ? 10  : PG_GETARG_INT32(4);
    rng       = PG_ARGISNULL(5) ? UINT64CONST(0x5851F42D4C957F2D)
                                : (uint64) PG_GETARG_INT64(5);

    if (steps < 1 || steps > GEN_MAX_STEPS)
        ereport(ERROR, (errmsg("walk_continuations: steps must be in [1,%d]", GEN_MAX_STEPS)));
    if (max_order < 1 || max_order > GEN_MAX_ORDER)
        ereport(ERROR, (errmsg("walk_continuations: max_order must be in [1,%d]", GEN_MAX_ORDER)));
    if (topk < 1 || topk > GEN_CAND_CAP)
        ereport(ERROR, (errmsg("walk_continuations: topk must be in [1,%d]", GEN_CAND_CAP)));
    if (ARR_NDIM(ctx_arr) != 1 || ARR_ELEMTYPE(ctx_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("walk_continuations: context must be a 1-D bytea array")));

    InitMaterializedSRF(fcinfo, 0);

    walk_cxt = CurrentMemoryContext;

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "walk_continuations: SPI_connect failed");
    ensure_plans();

    deconstruct_array(ctx_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &n_in);

    ctx_cap = n_in + steps;
    old = MemoryContextSwitchTo(walk_cxt);
    ctx      = (Datum *) palloc(sizeof(Datum) * (ctx_cap > 8 ? ctx_cap : 8));
    frontier = (Datum *) palloc(sizeof(Datum) * (n_in > 0 ? n_in : 1));
    cand     = (Cand *)  palloc(sizeof(Cand) * GEN_CAND_CAP);
    for (int i = 0; i < n_in; i++)
    {
        bytea *b;

        if (nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            ereport(ERROR, (errmsg("walk_continuations: context ids must be 16 bytes")));
        ctx[ctx_len++]           = copy_id_datum(elems[i]);
        frontier[n_frontier++]   = ctx[ctx_len - 1];
    }
    MemoryContextSwitchTo(old);

    if (ctx_len == 0)
    {
        SPI_finish();
        return (Datum) 0;
    }

    for (int32 step = 1; step <= steps; step++)
    {
        int  n_cand = 0, used = 0;
        int  pick = -1;

        CHECK_FOR_INTERRUPTS();

        /* ---- S6 PROPOSE: k-context backoff over the trajectories ---- */
        for (int k = (ctx_len < max_order ? ctx_len : max_order); k >= 1; k--)
        {
            ArrayType *tail;
            Datum      args[2];
            int        rc;

            tail = construct_array(ctx + ctx_len - k, k, BYTEAOID, -1, false,
                                   TYPALIGN_INT);
            args[0] = PointerGetDatum(tail);
            args[1] = Int32GetDatum(GEN_CAND_CAP);
            rc = SPI_execute_plan(propose_plan, args, NULL, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_continuations: propose failed: %s",
                     SPI_result_code_string(rc));

            if (SPI_processed > 0)
            {
                uint64 max = SPI_processed < GEN_CAND_CAP ? SPI_processed
                                                          : GEN_CAND_CAP;

                for (uint64 r = 0; r < max; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td  = SPI_tuptable->tupdesc;
                    bool      obj_null, sep_null, w_null;
                    Datum     od = SPI_getbinval(tup, td, 1, &obj_null);
                    Datum     sd = SPI_getbinval(tup, td, 2, &sep_null);
                    Datum     wd = SPI_getbinval(tup, td, 3, &w_null);

                    if (obj_null || w_null)
                        continue;
                    old = MemoryContextSwitchTo(walk_cxt);
                    cand[n_cand].obj    = copy_id_datum(od);
                    cand[n_cand].sep    = sep_null ? (Datum) 0 : copy_id_datum(sd);
                    MemoryContextSwitchTo(old);
                    cand[n_cand].weight = DatumGetInt64(wd);
                    cand[n_cand].steer  = 0.0;
                    cand[n_cand].edges  = 0;
                    n_cand++;
                }
                used = k;
                break;
            }
        }

        /* ---- FLOOR: consensus COMPLETES_TO when sequence is dry ---- */
        if (n_cand == 0)
        {
            Datum args[2];
            int   rc;

            args[0] = ctx[ctx_len - 1];
            args[1] = Int32GetDatum(topk);
            rc = SPI_execute_plan(floor_plan, args, NULL, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_continuations: consensus floor probe failed: %s",
                     SPI_result_code_string(rc));
            for (uint64 r = 0; r < SPI_processed && n_cand < GEN_CAND_CAP; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool      obj_null, w_null;
                Datum     od = SPI_getbinval(tup, td, 1, &obj_null);
                Datum     wd = SPI_getbinval(tup, td, 2, &w_null);

                if (obj_null || w_null)
                    continue;
                old = MemoryContextSwitchTo(walk_cxt);
                cand[n_cand].obj    = copy_id_datum(od);
                MemoryContextSwitchTo(old);
                cand[n_cand].sep    = (Datum) 0;
                cand[n_cand].weight = DatumGetInt64(wd);
                cand[n_cand].steer  = 0.0;
                cand[n_cand].edges  = 0;
                n_cand++;
            }
            used = 0;
        }
        if (n_cand == 0)
            break;

        /* ---- S7 STEER: re-rank by the live frontier ---- */
        {
            Datum     *objs;
            ArrayType *cand_a, *front_a;
            Datum      args[2];
            int        rc;

            objs = (Datum *) palloc(sizeof(Datum) * n_cand);
            for (int i = 0; i < n_cand; i++)
                objs[i] = cand[i].obj;
            cand_a  = construct_array(objs, n_cand, BYTEAOID, -1, false, TYPALIGN_INT);
            front_a = construct_array(frontier, n_frontier, BYTEAOID, -1, false, TYPALIGN_INT);

            args[0] = PointerGetDatum(cand_a);
            args[1] = PointerGetDatum(front_a);
            rc = SPI_execute_plan(steer_plan, args, NULL, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_continuations: steer failed: %s",
                     SPI_result_code_string(rc));

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool      c_null, s_null, e_null;
                Datum     cd = SPI_getbinval(tup, td, 1, &c_null);
                double    st = DatumGetFloat8(SPI_getbinval(tup, td, 2, &s_null));
                int64     ed = DatumGetInt64(SPI_getbinval(tup, td, 3, &e_null));
                bytea    *cb;

                if (c_null || s_null || e_null)
                    continue;
                cb = DatumGetByteaPP(cd);
                if (VARSIZE_ANY_EXHDR(cb) != 16)
                    continue;
                for (int i = 0; i < n_cand; i++)
                {
                    bytea *ob = DatumGetByteaPP(cand[i].obj);

                    if (memcmp(VARDATA_ANY(ob), VARDATA_ANY(cb), 16) == 0)
                    {
                        cand[i].steer = st;
                        cand[i].edges = ed;
                        break;
                    }
                }
            }
        }

        /*
         * Combine, signed. Refuted-toward-frontier candidates are EXCLUDED —
         * the walk must dead-end rather than walk into an adjudicated-negative
         * claim (walk_branches' rule). Unattested candidates keep their
         * sequence weight: no opinion is not a refutation.
         */
        {
            int m = 0;

            for (int i = 0; i < n_cand; i++)
            {
                if (cand[i].edges > 0 && cand[i].steer <= 0.0)
                    continue;
                cand[m] = cand[i];
                cand[m].eff = (double) cand[m].weight *
                              (cand[m].edges > 0 ? cand[m].steer : 1.0);
                m++;
            }
            n_cand = m;
        }
        if (n_cand == 0)
            break;

        /* partial selection sort of the top-k by eff */
        for (int i = 0; i < n_cand; i++)
        {
            int best = i;

            for (int j = i + 1; j < n_cand; j++)
                if (cand[j].eff > cand[best].eff) best = j;
            if (best != i)
            {
                Cand t = cand[i]; cand[i] = cand[best]; cand[best] = t;
            }
            if (i + 1 >= topk) break;
        }

        /* ---- S8 SAMPLE: Gumbel over the top-k at the caller's spread ---- */
        {
            int    limit = (n_cand < topk) ? n_cand : topk;
            double best_key = 0;

            for (int i = 0; i < limit; i++)
            {
                double u   = rng_uniform(&rng);
                double key = -log(u) / pow(cand[i].eff > 0.0 ? cand[i].eff : 1e-9,
                                           1.0 / (temp > 1e-6 ? temp : 1e-6));

                if (i == 0 || key < best_key)
                    { best_key = key; pick = i; }
            }
        }

        {
            Datum  values[4];
            bool   rnulls[4] = { false, false, false, false };

            values[0] = Int32GetDatum(step);
            values[1] = cand[pick].obj;
            values[2] = Int32GetDatum(used);
            if (cand[pick].sep != (Datum) 0)
                values[3] = cand[pick].sep;
            else
                rnulls[3] = true;
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
        }

        ctx[ctx_len++] = cand[pick].obj;
    }

    SPI_finish();
    return (Datum) 0;
}
