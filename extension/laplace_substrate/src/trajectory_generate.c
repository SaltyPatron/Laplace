/*
 * trajectory_generate.c — walk_continuations: the S6→S7→S8 emission loop
 * (docs/specs/36 §3), corpus-free.
 *
 * S6 PROPOSE  generation.trajectory_continuations($tail, NULL): the complete k-context successor
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
 * S7 STEER    generation.steer_candidates($cands, $frontier): re-rank by rated consensus
 *             mass reaching the LIVE frontier — the prompt's routed token/sense
 *             web PLUS the last
 *             max_order emitted constituents, so the frontier is where the walk has
 *             ARRIVED and not where it started (docs/specs/36 §3; GH #921 acceptance
 *             "each emitted unit updates the active frontier before the next election").
 *             steer_candidates.c's own header rejects "a weight fixed BEFORE the walk
 *             begins" as a prior rather than steering; holding the frontier at the
 *             prompt made that true of its only caller
 *             ids, re-scored per emitted token. Scored by walk_score.h, the
 *             same kernel walk_branches retrieves with, so proposing and
 *             steering cannot disagree about what an edge is worth.
 *
 *             The combination is signed and multiplicative, matching the
 *             walk's existing semantics (rank × edge_weight precedent, and
 *             walk_branches' "non-positive score must dead-end, not walk"):
 *               edges > 0, steer > 0  → sequence weight × steer
 *               edges = 0             → sequence weight × 1 only when S7 has
 *                                       no positively witnessed proposal;
 *                                       UNATTESTED is fallback, not refutation
 *               edges > 0, steer ≤ 0 → excluded (adjudicated against the
 *                                       frontier: refuted edges dead-end)
 *
 *             This ordering matters once the trajectory estate is large. If a
 *             witnessed-positive candidate exists, allowing an unattested but
 *             very frequent unigram continuation to compete at ×1 makes S6
 *             frequency erase S7 meaning. If no positive S7 signal exists, the
 *             unattested sequence pool remains available exactly as before.
 *
 * S8 SAMPLE   After steering the complete proposal set, a Gumbel draw over the
 *             top-k surviving candidates at the caller's
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
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"
#include "common/pg_prng.h"

#include "laplace/core/hash128.h"
#include "spi_common.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);

typedef struct Cand
{
    Datum  obj;        /* bytea(16), caller-context copy */
    Datum  sep;        /* bytea(16) or (Datum) 0         */
    int64  weight;     /* S6 sequence count              */
    double steer;      /* S7 signed consensus mass       */
    int64  edges;      /* S7 edge count; 0 = unattested  */
    double eff;        /* combined sampling weight       */
} Cand;

typedef struct CandIndex
{
    char key[16];
    int  index;
} CandIndex;

static SPIPlanPtr propose_plan = NULL;
static SPIPlanPtr floor_plan   = NULL;
static const char *steer_query =
    "SELECT candidate, steer, edges "
    "FROM generation.steer_candidates($1, $2)";

/*
 * Prepared once per backend and kept: the un-prepared path re-plans on every
 * emitted token of every walk. Proposal passes NULL deliberately: truncating
 * before S7 steering changes the answer.  The caller's top-k is applied only
 * after every exact-context candidate has its frontier score.
 */
static void
ensure_plans(void)
{
    if (propose_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAARRAYOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare_cursor(
            "SELECT object_id, sep_id, weight "
            "FROM generation.trajectory_continuations($1, $2)",
            2, argtypes, CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "walk_continuations: SPI_prepare(propose) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_continuations: SPI_keepplan(propose) failed");
        propose_plan = plan;
    }
    if (floor_plan == NULL)
    {
        Oid        argtypes[2] = { BYTEAOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare_cursor(
            "SELECT object_id, weight "
            "FROM generation.walk_completes_floor($1, $2)",
            2, argtypes, CURSOR_OPT_PARALLEL_OK);

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

static void
ensure_candidate_capacity(Cand **cand, int *capacity, uint64 needed,
                          MemoryContext owner)
{
    MemoryContext old;

    if (needed <= (uint64) *capacity)
        return;
    if (needed > (uint64) INT_MAX ||
        needed > (uint64) (MaxAllocSize / sizeof(Cand)))
        ereport(ERROR,
                (errmsg("walk_continuations: candidate set exceeds PostgreSQL allocation capacity"),
                 errdetail("Requested %llu candidates.",
                           (unsigned long long) needed)));

    old = MemoryContextSwitchTo(owner);
    *cand = *cand == NULL
        ? (Cand *) palloc(sizeof(Cand) * (Size) needed)
        : (Cand *) repalloc(*cand, sizeof(Cand) * (Size) needed);
    MemoryContextSwitchTo(old);
    *capacity = (int) needed;
}

static void
free_candidate_ids(Cand *cand, int count)
{
    for (int i = 0; i < count; i++)
    {
        pfree(DatumGetPointer(cand[i].obj));
        if (cand[i].sep != (Datum) 0)
            pfree(DatumGetPointer(cand[i].sep));
    }
}

static int
candidate_cmp(const void *a, const void *b)
{
    const Cand *x = (const Cand *) a;
    const Cand *y = (const Cand *) b;
    bytea      *xo;
    bytea      *yo;

    if (x->eff > y->eff) return -1;
    if (x->eff < y->eff) return 1;
    if (x->weight > y->weight) return -1;
    if (x->weight < y->weight) return 1;
    xo = DatumGetByteaPP(x->obj);
    yo = DatumGetByteaPP(y->obj);
    return memcmp(VARDATA_ANY(xo), VARDATA_ANY(yo), 16);
}

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *ctx_arr, *front_arr;
    int32      steps, max_order, topk;
    float8     temp;
    uint64     rng;
    Datum     *elems, *front_elems;
    bool      *nulls, *front_nulls;
    int        n_in, n_front_in;
    Datum     *ctx;
    int        ctx_len = 0, ctx_cap;
    Datum     *frontier;
    int        n_frontier = 0, n_prompt = 0;
    Cand      *cand = NULL;
    int        cand_capacity = 0;
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

    if (steps < 0)
        ereport(ERROR, (errmsg("walk_continuations: steps must not be negative")));
    if (max_order < 0)
        ereport(ERROR, (errmsg("walk_continuations: max_order must not be negative")));
    if (topk < 0)
        ereport(ERROR, (errmsg("walk_continuations: topk must not be negative")));
    if (!isfinite(temp) || temp < 0.0)
        ereport(ERROR, (errmsg("walk_continuations: spread must be finite and not negative")));
    if (ARR_NDIM(ctx_arr) != 1 || ARR_ELEMTYPE(ctx_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("walk_continuations: context must be a 1-D bytea array")));

    InitMaterializedSRF(fcinfo, 0);

    walk_cxt = CurrentMemoryContext;

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "walk_continuations: SPI_connect failed");
    ensure_plans();

    deconstruct_array(ctx_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &n_in);

    /* The ordered proposal context and the semantic frontier are different
     * operands. Appending routed neighbours to ctx would make them a fake
     * trajectory suffix. A caller that omits p_frontier retains the historical
     * prompt-only behaviour. */
    if (PG_NARGS() > 6 && !PG_ARGISNULL(6))
        front_arr = PG_GETARG_ARRAYTYPE_P(6);
    else
        front_arr = ctx_arr;
    if (ARR_NDIM(front_arr) != 1 || ARR_ELEMTYPE(front_arr) != BYTEAOID)
        ereport(ERROR, (errmsg("walk_continuations: frontier must be a 1-D bytea array")));
    deconstruct_array(front_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &front_elems, &front_nulls, &n_front_in);

    if ((uint64) n_in + (uint64) steps > (uint64) INT_MAX ||
        (uint64) n_in + (uint64) steps >
            (uint64) (MaxAllocSize / sizeof(Datum)))
        ereport(ERROR,
                (errmsg("walk_continuations: requested walk exceeds PostgreSQL allocation capacity")));
    if ((uint64) n_front_in + (uint64) max_order >
            (uint64) (MaxAllocSize / sizeof(Datum)))
        ereport(ERROR,
                (errmsg("walk_continuations: requested frontier exceeds PostgreSQL allocation capacity")));
    ctx_cap = n_in + steps;
    old = MemoryContextSwitchTo(walk_cxt);
    ctx      = (Datum *) palloc(sizeof(Datum) * (ctx_cap > 0 ? ctx_cap : 1));
    /* prompt content, held for the whole walk, plus a rolling window of the emitted
     * tail. The window is max_order — the SAME k the S6 context backoff already bounds
     * itself by — so the frontier introduces no constant of its own. */
    frontier = (Datum *) palloc(sizeof(Datum) *
                                (n_front_in + max_order > 0 ? n_front_in + max_order : 1));
    for (int i = 0; i < n_in; i++)
    {
        bytea *b;

        if (nulls[i])
            continue;
        b = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            ereport(ERROR, (errmsg("walk_continuations: context ids must be 16 bytes")));
        ctx[ctx_len++]           = copy_id_datum(elems[i]);
    }
    for (int i = 0; i < n_front_in; i++)
    {
        bytea *b;

        if (front_nulls[i])
            continue;
        b = DatumGetByteaPP(front_elems[i]);
        if (VARSIZE_ANY_EXHDR(b) != 16)
            ereport(ERROR, (errmsg("walk_continuations: frontier ids must be 16 bytes")));
        frontier[n_frontier++] = copy_id_datum(front_elems[i]);
    }
    /* An explicitly empty route is an abstention, not permission to erase the
     * request. Preserve the resolved prompt as the minimum live frontier. */
    if (n_frontier == 0)
    {
        for (int i = 0; i < ctx_len; i++)
            frontier[n_frontier++] = ctx[i];
    }
    n_prompt = n_frontier;
    MemoryContextSwitchTo(old);

    if (ctx_len == 0 || steps == 0 || topk == 0)
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
            char       argnulls[2] = { ' ', 'n' };
            int        rc;
            bool       found_candidates = false;

            tail = construct_array(ctx + ctx_len - k, k, BYTEAOID, -1, false,
                                   TYPALIGN_INT);
            args[0] = PointerGetDatum(tail);
            args[1] = (Datum) 0;
            rc = SPI_execute_plan(propose_plan, args, argnulls, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_continuations: propose failed: %s",
                     SPI_result_code_string(rc));

            if (SPI_processed > 0)
            {
                uint64 max = SPI_processed;

                ensure_candidate_capacity(&cand, &cand_capacity, max, walk_cxt);

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
                found_candidates = n_cand > 0;
            }
            if (SPI_tuptable != NULL)
                SPI_freetuptable(SPI_tuptable);
            pfree(tail);
            if (found_candidates)
            {
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
            ensure_candidate_capacity(&cand, &cand_capacity, SPI_processed, walk_cxt);
            for (uint64 r = 0; r < SPI_processed; r++)
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
            if (SPI_tuptable != NULL)
                SPI_freetuptable(SPI_tuptable);
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
            HASHCTL    ctl;
            HTAB      *by_id;

            objs = (Datum *) palloc(sizeof(Datum) * n_cand);
            for (int i = 0; i < n_cand; i++)
                objs[i] = cand[i].obj;
            cand_a  = construct_array(objs, n_cand, BYTEAOID, -1, false, TYPALIGN_INT);
            front_a = construct_array(frontier, n_frontier, BYTEAOID, -1, false, TYPALIGN_INT);

            args[0] = PointerGetDatum(cand_a);
            args[1] = PointerGetDatum(front_a);
            {
                Oid argtypes[2] = { BYTEAARRAYOID, BYTEAARRAYOID };

                rc = SPI_execute_with_args(steer_query, 2, argtypes, args,
                                           NULL, true, 0);
            }
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_continuations: steer failed: %s",
                     SPI_result_code_string(rc));

            memset(&ctl, 0, sizeof(ctl));
            ctl.keysize = 16;
            ctl.entrysize = sizeof(CandIndex);
            ctl.hcxt = walk_cxt;
            by_id = hash_create("walk continuation candidate index", n_cand,
                                &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
            for (int i = 0; i < n_cand; i++)
            {
                CandIndex *entry;
                bool       found;
                bytea     *object = DatumGetByteaPP(cand[i].obj);

                entry = (CandIndex *) hash_search(by_id, VARDATA_ANY(object),
                                                   HASH_ENTER, &found);
                if (!found)
                    entry->index = i;
            }

            for (uint64 r = 0; r < SPI_processed; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool      c_null, s_null, e_null;
                Datum     cd = SPI_getbinval(tup, td, 1, &c_null);
                double    st = DatumGetFloat8(SPI_getbinval(tup, td, 2, &s_null));
                int64     ed = DatumGetInt64(SPI_getbinval(tup, td, 3, &e_null));
                bytea    *cb;
                CandIndex *entry;

                if (c_null || s_null || e_null)
                    continue;
                cb = DatumGetByteaPP(cd);
                if (VARSIZE_ANY_EXHDR(cb) != 16)
                    continue;
                entry = (CandIndex *) hash_search(by_id, VARDATA_ANY(cb),
                                                   HASH_FIND, NULL);
                if (entry != NULL)
                {
                    cand[entry->index].steer = st;
                    cand[entry->index].edges = ed;
                }
            }
            if (SPI_tuptable != NULL)
                SPI_freetuptable(SPI_tuptable);
            hash_destroy(by_id);
            pfree(objs);
            pfree(cand_a);
            pfree(front_a);
        }

        /*
         * Combine, signed, in two semantic pools. Refuted-toward-frontier
         * candidates are always excluded. If S7 positively witnesses at least
         * one proposal, unattested sequence-only proposals wait behind that
         * witnessed pool instead of competing with raw ×1 frequency. If S7 has
         * no positive signal at all, unattested proposals remain the fallback.
         *
         * This keeps "no opinion" distinct from refutation without letting a
         * high-frequency unigram erase meaning when meaning is actually present.
         */
        {
            int  m = 0;
            bool has_positive_steer = false;

            for (int i = 0; i < n_cand; i++)
            {
                if (cand[i].weight > 0 && cand[i].edges > 0
                    && cand[i].steer > 0.0 && isfinite(cand[i].steer))
                {
                    has_positive_steer = true;
                    break;
                }
            }

            for (int i = 0; i < n_cand; i++)
            {
                if ((cand[i].edges > 0
                     && (cand[i].steer <= 0.0 || !isfinite(cand[i].steer)))
                    || (has_positive_steer && cand[i].edges == 0))
                {
                    pfree(DatumGetPointer(cand[i].obj));
                    if (cand[i].sep != (Datum) 0)
                        pfree(DatumGetPointer(cand[i].sep));
                    continue;
                }
                cand[m] = cand[i];
                cand[m].eff = (double) cand[m].weight *
                              (cand[m].edges > 0 ? cand[m].steer : 1.0);
                if (cand[m].eff <= 0.0 || !isfinite(cand[m].eff))
                {
                    pfree(DatumGetPointer(cand[m].obj));
                    if (cand[m].sep != (Datum) 0)
                        pfree(DatumGetPointer(cand[m].sep));
                    continue;
                }
                m++;
            }
            n_cand = m;
        }
        if (n_cand == 0)
            break;

        qsort(cand, (size_t) n_cand, sizeof(Cand), candidate_cmp);

        /* ---- S8 SAMPLE: Gumbel over the top-k at the caller's spread ---- */
        {
            int    limit = (n_cand < topk) ? n_cand : topk;
            double best_key = 0;

            if (temp == 0.0)
                pick = 0;
            for (int i = 0; temp > 0.0 && i < limit; i++)
            {
                double u   = rng_uniform(&rng);
                double key = log(cand[i].eff) / temp - log(-log(u));

                if (i == 0 || key > best_key)
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

        old = MemoryContextSwitchTo(walk_cxt);
        ctx[ctx_len++] = copy_id_datum(cand[pick].obj);
        MemoryContextSwitchTo(old);

        /* ---- advance the frontier S7 steers toward ----
         * The prompt stays: it is the request, and consensus mass reaching it must keep
         * counting. What changes is the tail — the emitted constituents, oldest dropped
         * once the window is full, so |cands| x |frontier| stays bounded exactly as
         * steer_candidates.c requires for its single round trip per token. */
        if (max_order > 0 && n_frontier - n_prompt >= max_order)
        {
            memmove(&frontier[n_prompt], &frontier[n_prompt + 1],
                    sizeof(Datum) * (size_t) (n_frontier - n_prompt - 1));
            n_frontier--;
        }
        if (max_order > 0)
            frontier[n_frontier++] = ctx[ctx_len - 1];
        free_candidate_ids(cand, n_cand);
    }

    SPI_finish();
    return (Datum) 0;
}
