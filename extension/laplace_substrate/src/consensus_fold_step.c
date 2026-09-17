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
    int64_t         tau;
    size_t          group_count;
    size_t          group_capacity;
    int64_t        *opponent_ratings;
    int64_t        *opponent_phis;
    int64_t        *games;
    int64_t        *score_sums;
} ConsensusFoldState;

PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_step);
PG_FUNCTION_INFO_V1(pg_laplace_consensus_fold_step_v2);

static void
consensus_fold_reserve(ConsensusFoldState *state, MemoryContext aggcontext,
                       size_t required)
{
    size_t next_capacity;
    int64_t *new_opponents;
    int64_t *new_phis;
    int64_t *new_games;
    int64_t *new_sums;

    if (required <= state->group_capacity)
        return;

    next_capacity = state->group_capacity == 0 ? 8 : state->group_capacity;
    while (next_capacity < required)
    {
        if (next_capacity > SIZE_MAX / 2)
            ereport(ERROR,
                    (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                     errmsg("consensus_fold: too many rating-period groups")));
        next_capacity *= 2;
    }
    if (next_capacity > MaxAllocSize / sizeof(int64_t))
        ereport(ERROR,
                (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                 errmsg("consensus_fold: rating-period group storage exceeds allocation capacity")));

    new_opponents = MemoryContextAlloc(aggcontext, next_capacity * sizeof(int64_t));
    new_phis = MemoryContextAlloc(aggcontext, next_capacity * sizeof(int64_t));
    new_games = MemoryContextAlloc(aggcontext, next_capacity * sizeof(int64_t));
    new_sums = MemoryContextAlloc(aggcontext, next_capacity * sizeof(int64_t));

    if (state->group_count > 0)
    {
        size_t bytes = state->group_count * sizeof(int64_t);
        memcpy(new_opponents, state->opponent_ratings, bytes);
        memcpy(new_phis, state->opponent_phis, bytes);
        memcpy(new_games, state->games, bytes);
        memcpy(new_sums, state->score_sums, bytes);
    }

    if (state->opponent_ratings) pfree(state->opponent_ratings);
    if (state->opponent_phis) pfree(state->opponent_phis);
    if (state->games) pfree(state->games);
    if (state->score_sums) pfree(state->score_sums);

    state->opponent_ratings = new_opponents;
    state->opponent_phis = new_phis;
    state->games = new_games;
    state->score_sums = new_sums;
    state->group_capacity = next_capacity;
}

static Datum
consensus_fold_step(FunctionCallInfo fcinfo, bool current)
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
    games = PG_GETARG_INT64(current ? 7 : 6);

    if (is_seed) {
        if (state->any || state->group_count != 0)
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
        int64_t opponent_rating = current
            ? PG_GETARG_INT64(5)
            : CONSENSUS_FOLD_NEUTRAL_MU;
        if (opponent_rating == 0)
            opponent_rating = CONSENSUS_FOLD_NEUTRAL_MU;
        int64_t phi       = PG_GETARG_INT64(current ? 6 : 5);
        int64_t sum_score = PG_GETARG_INT64(current ? 8 : 7);
        int tau_arg       = current ? 9 : 8;
        int64_t tau       = PG_ARGISNULL(tau_arg) ? LAPLACE_GLICKO2_DEFAULT_TAU
                                                  : PG_GETARG_INT64(tau_arg);
        size_t slot;

        if (games <= 0)
            ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold: games must be > 0 (got %ld)", (long) games)));
        if (phi < 0)
            ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold: opponent rd must be >= 0 (got %ld)", (long) phi)));
        if (sum_score < 0 || (__int128)sum_score > (__int128)games * 1000000000LL)
            ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold: score sum is outside the rating-period range"),
                 errdetail("games=%ld sum_score=%ld", (long) games, (long) sum_score)));
        if (tau <= 0)
            ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold: tau must be > 0 (got %ld)", (long) tau)));

        if (!state->any) {
            glicko2_init(&state->st,
                         CONSENSUS_FOLD_NEUTRAL_MU,
                         CONSENSUS_FOLD_INITIAL_RD,
                         CONSENSUS_FOLD_INITIAL_VOLATILITY);
            state->witness_count = 0;
            state->any = true;
        }

        if (state->group_count == 0)
            state->tau = tau;
        else if (state->tau != tau)
            ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("consensus_fold: all groups in one rating period must use the same tau"),
                 errdetail("first_tau=%ld row_tau=%ld", (long) state->tau, (long) tau)));

        if (state->witness_count > PG_INT64_MAX - games)
            ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("consensus_fold: witness count exceeds bigint capacity")));
        state->witness_count += games;

        consensus_fold_reserve(state, aggcontext, state->group_count + 1);
        slot = state->group_count++;
        state->opponent_ratings[slot] = opponent_rating;
        state->opponent_phis[slot] = phi;
        state->games[slot] = games;
        state->score_sums[slot] = sum_score;
    }

    PG_RETURN_POINTER(state);
}

Datum
pg_laplace_consensus_fold_step(PG_FUNCTION_ARGS)
{
    return consensus_fold_step(fcinfo, false);
}

Datum
pg_laplace_consensus_fold_step_v2(PG_FUNCTION_ARGS)
{
    return consensus_fold_step(fcinfo, true);
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

    /* One aggregate invocation is one Glicko rating period.  Every source/
     * opponent partial belongs to that period and must be accumulated before
     * the state transition.  Applying each aggregate row immediately made the
     * result order-sensitive and repeatedly shrank RD as though one logical
     * period were many sequential periods; sufficiently large later partials
     * could then drive a valid fold into the solver's inadmissible/stall path.
     * The native write lane already uses this grouped-period kernel. */
    if (state->group_count > 0 &&
        glicko2_fold_grouped_period(&state->st,
                                    state->opponent_ratings,
                                    state->opponent_phis,
                                    state->games,
                                    state->score_sums,
                                    state->group_count,
                                    state->tau,
                                    0) != 0)
        ereport(ERROR,
            (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
             errmsg("consensus_fold: grouped rating-period update failed"),
             errdetail("groups=%zu witness_count=%ld",
                       state->group_count, (long) state->witness_count)));

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
