/*
 * extension/laplace_substrate/src/laplace_substrate.c
 *
 * Thin PG_FUNCTION_INFO_V1 wrappers for the laplace_substrate extension
 * — DB calls engine, no DB-side math beyond aggregate
 * accumulation. The aggregate state is an int64 fixed-point glicko2_state_t
 * + a growing observation buffer; FINALFUNC delegates to glicko2_update_period
 * for the full Glickman 2013 rating-period algorithm.
 */

#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "executor/spi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "access/htup_details.h"
#include "catalog/pg_type.h"
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
/* Glicko-2 aggregate.                                     */
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
/* arena semantics, prior_* + tau are constant for a given      */
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

/* ------------------------------------------------------------------------- */
/* entities_exist_bitmap (Story D.3 / #250 / Framework Epic #232).           */
/*                                                                           */
/* SQL signature:                                                            */
/*   laplace.entities_exist_bitmap(ids bytea[]) RETURNS bytea                */
/*                                                                           */
/* For N candidate IDs, returns a packed bitmap of ceil(N/8) bytes where     */
/* bit i (LSB-first within each byte) is set iff candidates[i] is already    */
/* in laplace.entities.                                                      */
/*                                                                           */
/* Implementation: single SPI execute joining unnest(WITH ORDINALITY)        */
/* against the indexed entities.id; iterate result and set bits. One DB      */
/* round-trip (the SPI execute) regardless of candidate count.               */
/* ------------------------------------------------------------------------- */

PG_FUNCTION_INFO_V1(pg_laplace_entities_exist_bitmap);

Datum
pg_laplace_entities_exist_bitmap(PG_FUNCTION_ARGS)
{
    ArrayType*  ids_array;
    int         candidate_count;
    int         bitmap_bytes;
    bytea*      result;
    uint8*      bm;
    Oid         argtypes[1];
    Datum       args[1];
    int         spi_rc;
    uint64      i;

    if (PG_ARGISNULL(0))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("entities_exist_bitmap: ids array must not be NULL")));

    ids_array = PG_GETARG_ARRAYTYPE_P(0);

    if (ARR_NDIM(ids_array) > 1)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("entities_exist_bitmap: ids array must be 1-dimensional")));

    if (ARR_ELEMTYPE(ids_array) != BYTEAOID)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("entities_exist_bitmap: ids array element type must be bytea")));

    if (ARR_HASNULL(ids_array))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("entities_exist_bitmap: ids array must not contain NULL")));

    candidate_count = ARR_NDIM(ids_array) == 0
                      ? 0
                      : ArrayGetNItems(ARR_NDIM(ids_array), ARR_DIMS(ids_array));

    bitmap_bytes = (candidate_count + 7) / 8;

    result = (bytea*) palloc(VARHDRSZ + bitmap_bytes);
    SET_VARSIZE(result, VARHDRSZ + bitmap_bytes);
    if (bitmap_bytes > 0)
        memset(VARDATA(result), 0, bitmap_bytes);
    bm = (uint8*) VARDATA(result);

    if (candidate_count == 0)
        PG_RETURN_BYTEA_P(result);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("entities_exist_bitmap: SPI_connect failed")));

    argtypes[0] = BYTEAARRAYOID;
    args[0]     = PointerGetDatum(ids_array);

    spi_rc = SPI_execute_with_args(
        "SELECT (u.ord - 1)::int "
        "FROM unnest($1::bytea[]) WITH ORDINALITY u(id, ord) "
        "JOIN laplace.entities e ON e.id = u.id",
        1, argtypes, args, NULL,
        true /* read_only */, 0 /* unlimited */);

    if (spi_rc != SPI_OK_SELECT)
    {
        SPI_finish();
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("entities_exist_bitmap: SPI_execute_with_args failed (rc=%d)", spi_rc)));
    }

    for (i = 0; i < SPI_processed; i++)
    {
        bool   isnull;
        Datum  d = SPI_getbinval(SPI_tuptable->vals[i],
                                  SPI_tuptable->tupdesc,
                                  1, &isnull);
        if (!isnull)
        {
            int pos = DatumGetInt32(d);
            if (pos >= 0 && pos < candidate_count)
            {
                bm[pos >> 3] |= (uint8)(1u << (pos & 7u));
            }
        }
    }

    SPI_finish();
    PG_RETURN_BYTEA_P(result);
}

/*
 * _PG_init — called when the extension is loaded.
 *: laplace_dynamics_init() locks MKL threading layer to TBB
 * and sets MKL_CBWR for substrate determinism. Idempotent.
 */
void _PG_init(void);
void
_PG_init(void)
{
    (void)laplace_dynamics_init();
}
