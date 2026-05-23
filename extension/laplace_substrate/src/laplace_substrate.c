/*
 * extension/laplace_substrate/src/laplace_substrate.c
 *
 * Thin PG_FUNCTION_INFO_V1 wrappers for the laplace_substrate extension
 * per RULES.md R6 — DB calls engine, no DB-side math beyond aggregate
 * accumulation. The aggregate state is an int64 fixed-point glicko2_state_t
 * + a growing observation buffer; FINALFUNC delegates to glicko2_update_period
 * for the full Glickman 2013 rating-period algorithm.
 */

#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "access/htup_details.h"
#include "nodes/execnodes.h"

#include "laplace/core/version.h"
#include "laplace/core/glicko2.h"
#include "laplace/dynamics/init.h"

PG_MODULE_MAGIC;

PG_FUNCTION_INFO_V1(pg_laplace_substrate_version);

Datum
pg_laplace_substrate_version(PG_FUNCTION_ARGS)
{
    /* Returns the core version string. Proves laplace_substrate links
     * BOTH liblaplace_core AND liblaplace_dynamics. */
    const char* v = laplace_core_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}

/* ------------------------------------------------------------------------- */
/* Glicko-2 aggregate (Story 5.6 / #68).                                     */
/*                                                                           */
/* Aggregate signature (per CREATE AGGREGATE in sql/06_glicko2.sql.in):      */
/*                                                                           */
/*   laplace_glicko2_accumulate(                                             */
/*       prior_rating     bigint,  -- fixed-point ×1e9 (Glicko-1 scale)      */
/*       prior_rd         bigint,  -- fixed-point ×1e9 (Glicko-1 scale)      */
/*       prior_volatility bigint,  -- fixed-point ×1e9                       */
/*       opponent_rating  bigint,  -- per-row observation                    */
/*       opponent_rd      bigint,                                            */
/*       score            bigint,  -- 0=loss, 5e8=draw, 1e9=win              */
/*       tau              bigint   -- system constant ×1e9                   */
/*   ) RETURNS laplace_glicko2_result                                        */
/*                                                                           */
/* Per ADR 0036 arena semantics, prior_* + tau are constant for a given      */
/* attestation update — supplied per row but only read from the FIRST row    */
/* (initialized flag short-circuits subsequent rows). The aggregate is       */
/* commutative within a rating period per Glickman §"Step 7".                */
/* ------------------------------------------------------------------------- */

typedef struct {
    bool                     initialized;
    glicko2_state_t          prior;
    int64_t                  tau;
    glicko2_observation_t*   obs;
    size_t                   obs_len;
    size_t                   obs_cap;
} LaplaceGlicko2AggState;

#define LAPLACE_GLICKO2_OBS_INITIAL_CAP 16

PG_FUNCTION_INFO_V1(pg_laplace_glicko2_sfunc);

Datum
pg_laplace_glicko2_sfunc(PG_FUNCTION_ARGS)
{
    MemoryContext            aggcontext;
    MemoryContext            oldcontext;
    LaplaceGlicko2AggState*  state;

    if (!AggCheckCallContext(fcinfo, &aggcontext))
        elog(ERROR, "laplace_glicko2_sfunc called outside aggregate context");

    if (PG_ARGISNULL(0)) {
        oldcontext = MemoryContextSwitchTo(aggcontext);
        state = (LaplaceGlicko2AggState*) palloc0(sizeof(LaplaceGlicko2AggState));
        state->obs_cap = LAPLACE_GLICKO2_OBS_INITIAL_CAP;
        state->obs = (glicko2_observation_t*)
            palloc(sizeof(glicko2_observation_t) * state->obs_cap);
        MemoryContextSwitchTo(oldcontext);
    } else {
        state = (LaplaceGlicko2AggState*) PG_GETARG_POINTER(0);
    }

    /* First non-NULL prior wins; remember and never re-read prior args. */
    if (!state->initialized) {
        if (PG_ARGISNULL(1) || PG_ARGISNULL(2) || PG_ARGISNULL(3))
            ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("laplace_glicko2_accumulate: prior_rating, prior_rd, "
                        "prior_volatility must all be non-NULL on the first row")));

        glicko2_init(&state->prior,
                     PG_GETARG_INT64(1),
                     PG_GETARG_INT64(2),
                     PG_GETARG_INT64(3));
        state->tau = PG_ARGISNULL(7)
                     ? LAPLACE_GLICKO2_DEFAULT_TAU
                     : PG_GETARG_INT64(7);
        state->initialized = true;
    }

    /* Skip rows with a NULL observation triple — useful for LEFT JOINs
     * where an entity has zero observations in the period; the rating-
     * period decay path still runs in FINALFUNC. */
    if (PG_ARGISNULL(4) || PG_ARGISNULL(5) || PG_ARGISNULL(6))
        PG_RETURN_POINTER(state);

    if (state->obs_len == state->obs_cap) {
        oldcontext = MemoryContextSwitchTo(aggcontext);
        state->obs_cap *= 2;
        state->obs = (glicko2_observation_t*) repalloc(
            state->obs, sizeof(glicko2_observation_t) * state->obs_cap);
        MemoryContextSwitchTo(oldcontext);
    }

    state->obs[state->obs_len].opponent_rating = PG_GETARG_INT64(4);
    state->obs[state->obs_len].opponent_rd     = PG_GETARG_INT64(5);
    state->obs[state->obs_len].score           = PG_GETARG_INT64(6);
    state->obs_len++;

    PG_RETURN_POINTER(state);
}

PG_FUNCTION_INFO_V1(pg_laplace_glicko2_finalfunc);

Datum
pg_laplace_glicko2_finalfunc(PG_FUNCTION_ARGS)
{
    LaplaceGlicko2AggState*  state;
    glicko2_state_t          result;
    TupleDesc                tupdesc;
    Datum                    values[3];
    bool                     nulls[3] = { false, false, false };
    HeapTuple                tuple;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();

    state = (LaplaceGlicko2AggState*) PG_GETARG_POINTER(0);

    /* Aggregate received zero rows OR a row that never carried a usable prior. */
    if (!state->initialized)
        PG_RETURN_NULL();

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
             errmsg("function returning record called in context "
                    "that cannot accept type record")));
    BlessTupleDesc(tupdesc);

    /* Copy prior into a local result; do not mutate aggcontext state
     * (PG may invoke FINALFUNC repeatedly under some plans). now_ns = 0
     * keeps the aggregate output deterministic; callers set last_observed_at
     * explicitly when persisting back into the attestations table. */
    result = state->prior;
    glicko2_update_period(&result, state->obs, state->obs_len,
                          state->tau, /* now_ns */ 0);

    values[0] = Int64GetDatum(result.rating);
    values[1] = Int64GetDatum(result.rd);
    values[2] = Int64GetDatum(result.volatility);

    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}

/*
 * _PG_init — called when the extension is loaded.
 * Per ADR 0030: laplace_dynamics_init() locks MKL threading layer to TBB
 * and sets MKL_CBWR for substrate determinism. Idempotent.
 */
void _PG_init(void);
void
_PG_init(void)
{
    (void)laplace_dynamics_init();
}
