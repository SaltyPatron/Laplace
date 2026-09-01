






















#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/memutils.h"
#include "access/htup_details.h"
#include "nodes/execnodes.h"

#include "laplace/core/glicko2.h"




#include "consensus_fold_math.h"

typedef struct {
    bool            seeded;      
    bool            any;         
    glicko2_state_t st;
    int64_t         witness_count;
} ConsensusFoldState;

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_step);











Datum
pg_laplace_consensus_fold_step(PG_FUNCTION_ARGS)
{
    MemoryContext           aggcontext;
    ConsensusFoldState* state;
    bool                    is_seed;
    int64_t                 games;

    if (!AggCheckCallContext(fcinfo, &aggcontext))
        elog(ERROR, "consensus_fold_step called outside aggregate context");

    if (PG_ARGISNULL(0)) {
        state = (ConsensusFoldState*)
            MemoryContextAllocZero(aggcontext, sizeof(ConsensusFoldState));
    } else {
        state = (ConsensusFoldState*) PG_GETARG_POINTER(0);
    }

    is_seed = PG_GETARG_BOOL(1);
    /* The current aggregate carries opponent rating and RD. Keep accepting the
     * installed legacy arity during an in-place upgrade; that overload has no
     * opponent rating and therefore retains the historical neutral value. */
    games   = PG_GETARG_INT64(PG_NARGS() >= 10 ? 7 : 6);

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
        bool current = PG_NARGS() >= 10;
        int64_t opponent_rating = current
            ? PG_GETARG_INT64(5)
            : CONSENSUS_FOLD_NEUTRAL_MU;
        int64_t phi       = PG_GETARG_INT64(current ? 6 : 5);
        int64_t sum_score = PG_GETARG_INT64(current ? 8 : 7);
        int tau_arg       = current ? 9 : 8;
        int64_t tau       = PG_ARGISNULL(tau_arg) ? LAPLACE_GLICKO2_DEFAULT_TAU
                                                  : PG_GETARG_INT64(tau_arg);

        if (games <= 0)
            ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold: games must be > 0 (got %ld)", (long) games)));
        if (!state->any) {
            glicko2_init(&state->st,
                         CONSENSUS_FOLD_NEUTRAL_MU,
                         CONSENSUS_FOLD_INITIAL_RD,
                         CONSENSUS_FOLD_INITIAL_VOLATILITY);
            state->witness_count = 0;
            state->any = true;
        }


        if (consensus_fold_apply_partial(&state->st, opponent_rating, phi, games,
                                         sum_score, tau) != 0)
            ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("consensus_fold: aggregate exceeds fixed-point capacity"),
                 errdetail("games=%ld sum_score=%ld", (long) games,
                           (long) sum_score)));

        if (state->witness_count > PG_INT64_MAX - games)
            ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("consensus_fold: witness count exceeds bigint capacity")));
        state->witness_count += games;
    }

    PG_RETURN_POINTER(state);
}

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_final);

Datum
pg_laplace_consensus_fold_final(PG_FUNCTION_ARGS)
{
    ConsensusFoldState* state;
    TupleDesc               tupdesc;
    Datum                   values[4];
    bool                    nulls[4] = { false, false, false, false };
    HeapTuple               tuple;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    state = (ConsensusFoldState*) PG_GETARG_POINTER(0);
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
