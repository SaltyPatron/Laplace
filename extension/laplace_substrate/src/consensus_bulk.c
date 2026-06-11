/*
 * consensus_bulk.c — the PK-less bulk fold lane (OPEN-PROBLEMS §11, HANDOFF-fold-lane).
 *
 * The merge fold pays 2 random index probes per relation against a consensus
 * working set that exceeds RAM, re-touched 3-4x across Glicko periods. This lane
 * replaces probes with one external sort: all staged epochs UNION the existing
 * consensus rows (as period-0 seeds), GROUP BY relation identity with the epochs
 * folded IN ORDER through this aggregate — one fmgr transition per (relation,
 * period partial), zero per-row index probes, sequential I/O end to end. The
 * result lands in a fresh heap whose PK is built once, then atomically swapped
 * in (finish_consensus_bulk, 14_period_fold.sql.in). Production indexes on the
 * live table are never dropped or degraded; readers see the old consensus until
 * the swap commits.
 *
 * Laws carried over from the merge lane (bit-identical by construction):
 *   - epochs fold strictly in order (ORDER BY inside the aggregate call);
 *   - one φ per relation per period (the per-(relation,epoch) pre-merge guards
 *     mixed φ before rows reach this aggregate; a differing φ across epochs is
 *     legal and handled here exactly as sequential merge folds would);
 *   - the observation split (q/rem) matches pg_laplace_glicko2_accumulate_games
 *     exactly, so ratings are int64-identical to the merge lane's output.
 */
#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/memutils.h"
#include "access/htup_details.h"
#include "nodes/execnodes.h"

#include "laplace/core/glicko2.h"

/* The neutral prior, fp 1e9 — MUST match 13_mu_law.sql.in (glicko2_neutral_mu /
 * glicko2_initial_rd / glicko2_initial_volatility); the regress lane-parity pin
 * exists to catch drift between these and the SQL constants. */
#define CONSENSUS_BULK_NEUTRAL_MU         INT64CONST(1500000000000)
#define CONSENSUS_BULK_INITIAL_RD         INT64CONST(350000000000)
#define CONSENSUS_BULK_INITIAL_VOLATILITY INT64CONST(60000000)

typedef struct {
    bool            seeded;      /* saw the period-0 seed row (existing consensus) */
    bool            any;         /* state holds at least one input                 */
    glicko2_state_t st;
    int64_t         witness_count;
} ConsensusBulkFoldState;

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_step);

/*
 * Transition: (state internal,
 *              is_seed bool,
 *              v1, v2, v3 bigint,   -- seed: rating, rd, volatility
 *              phi bigint,          -- partial: opponent φ for the period
 *              games bigint,        -- seed: prior witness_count; partial: games
 *              sum_score bigint,    -- partial: Σ score (fp 1e9)
 *              tau bigint)
 * Inputs MUST arrive seed-first then epochs ascending (ORDER BY in the call).
 */
Datum
pg_laplace_consensus_fold_step(PG_FUNCTION_ARGS)
{
    MemoryContext           aggcontext;
    ConsensusBulkFoldState* state;
    bool                    is_seed;
    int64_t                 games;

    if (!AggCheckCallContext(fcinfo, &aggcontext))
        elog(ERROR, "consensus_fold_step called outside aggregate context");

    if (PG_ARGISNULL(0)) {
        state = (ConsensusBulkFoldState*)
            MemoryContextAllocZero(aggcontext, sizeof(ConsensusBulkFoldState));
    } else {
        state = (ConsensusBulkFoldState*) PG_GETARG_POINTER(0);
    }

    is_seed = PG_GETARG_BOOL(1);
    games   = PG_GETARG_INT64(6);

    if (is_seed) {
        if (state->any)
            ereport(ERROR,
                (errcode(ERRCODE_DATA_EXCEPTION),
                 errmsg("consensus_fold: seed row arrived after period partials "
                        "(ORDER BY violated)")));
        glicko2_init(&state->st,
                     PG_GETARG_INT64(2),
                     PG_GETARG_INT64(3),
                     PG_GETARG_INT64(4));
        state->witness_count = games;
        state->seeded = true;
        state->any = true;
        PG_RETURN_POINTER(state);
    }

    {
        glicko2_observation_t* obs;
        int64_t sum_score = PG_GETARG_INT64(7);
        int64_t phi       = PG_GETARG_INT64(5);
        int64_t tau       = PG_ARGISNULL(8) ? LAPLACE_GLICKO2_DEFAULT_TAU
                                            : PG_GETARG_INT64(8);
        int64_t q, rem, i;

        if (games <= 0)
            ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold: games must be > 0 (got %ld)", (long) games)));
        if (games > (INT64CONST(1) << 27))
            ereport(ERROR,
                (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                 errmsg("consensus_fold: %ld games in one period exceeds the "
                        "per-relation bound", (long) games)));

        if (!state->any) {
            glicko2_init(&state->st,
                         CONSENSUS_BULK_NEUTRAL_MU,
                         CONSENSUS_BULK_INITIAL_RD,
                         CONSENSUS_BULK_INITIAL_VOLATILITY);
            state->witness_count = 0;
            state->any = true;
        }

        /* The exact observation split of pg_laplace_glicko2_accumulate_games. */
        obs = (glicko2_observation_t*)
            palloc(sizeof(glicko2_observation_t) * (Size) games);
        q   = sum_score / games;
        rem = sum_score - q * (games - 1);
        for (i = 0; i < games - 1; i++) {
            obs[i].opponent_rating = CONSENSUS_BULK_NEUTRAL_MU;
            obs[i].opponent_rd     = phi;
            obs[i].score           = q;
        }
        obs[games - 1].opponent_rating = CONSENSUS_BULK_NEUTRAL_MU;
        obs[games - 1].opponent_rd     = phi;
        obs[games - 1].score           = rem;

        glicko2_update_period(&state->st, obs, (size_t) games, tau, 0);
        pfree(obs);

        state->witness_count += games;
    }

    PG_RETURN_POINTER(state);
}

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_final);

Datum
pg_laplace_consensus_fold_final(PG_FUNCTION_ARGS)
{
    ConsensusBulkFoldState* state;
    TupleDesc               tupdesc;
    Datum                   values[4];
    bool                    nulls[4] = { false, false, false, false };
    HeapTuple               tuple;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    state = (ConsensusBulkFoldState*) PG_GETARG_POINTER(0);
    if (!state->any)
        PG_RETURN_NULL();

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
             errmsg("function returning record called in context "
                    "that cannot accept type record")));
    BlessTupleDesc(tupdesc);

    values[0] = Int64GetDatum(state->st.rating);
    values[1] = Int64GetDatum(state->st.rd);
    values[2] = Int64GetDatum(state->st.volatility);
    values[3] = Int64GetDatum(state->witness_count);

    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}
