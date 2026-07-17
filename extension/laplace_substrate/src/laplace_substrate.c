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
#include "laplace/core/score.h"
#include "laplace/core/hash128.h"

#include "descent_probe.h"
#include "perfcache_native.h"
#include "laplace/core/relation_law.h"
#include "laplace/dynamics/init.h"

#include "perfcache_native.h"
#include "trajectory_corpus.h"
#include "spi_common.h"

PG_MODULE_MAGIC;

PG_FUNCTION_INFO_V1(pg_laplace_substrate_version);

Datum
pg_laplace_substrate_version(PG_FUNCTION_ARGS)
{
    const char* v = laplace_core_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}

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

    if (!state->initialized)
        PG_RETURN_NULL();

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
             errmsg("function returning record called in context "
                    "that cannot accept type record")));
    BlessTupleDesc(tupdesc);

    result = state->prior;
    glicko2_update_period(&result, state->obs, state->obs_len,
                          state->tau, 0);

    values[0] = Int64GetDatum(result.rating);
    values[1] = Int64GetDatum(result.rd);
    values[2] = Int64GetDatum(result.volatility);

    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}

PG_FUNCTION_INFO_V1(pg_laplace_score);

Datum
pg_laplace_score(PG_FUNCTION_ARGS)
{
    PG_RETURN_INT64(laplace_score_fp(PG_GETARG_FLOAT8(0), PG_GETARG_FLOAT8(1)));
}




PG_FUNCTION_INFO_V1(pg_laplace_effective_mu);

Datum
pg_laplace_effective_mu(PG_FUNCTION_ARGS)
{
    PG_RETURN_INT64(laplace_effective_mu_fp(PG_GETARG_INT64(0), PG_GETARG_INT64(1)));
}



PG_FUNCTION_INFO_V1(pg_laplace_glicko2_neutral_mu);

Datum
pg_laplace_glicko2_neutral_mu(PG_FUNCTION_ARGS)
{
    PG_RETURN_INT64(laplace_glicko2_neutral_mu_fp());
}

PG_FUNCTION_INFO_V1(pg_laplace_score_inverse);

Datum
pg_laplace_score_inverse(PG_FUNCTION_ARGS)
{
    PG_RETURN_FLOAT8(laplace_score_inverse_fp(PG_GETARG_INT64(0), PG_GETARG_FLOAT8(1)));
}

PG_FUNCTION_INFO_V1(pg_laplace_glicko2_accumulate_games);

Datum
pg_laplace_glicko2_accumulate_games(PG_FUNCTION_ARGS)
{
    glicko2_state_t         st;
    int64_t                 games;
    int64_t                 sum_score;
    int64_t                 opp_rating, opp_rd, tau;
    TupleDesc               tupdesc;
    Datum                   values[3];
    bool                    nulls[3] = { false, false, false };
    HeapTuple               tuple;

    glicko2_init(&st,
                 PG_GETARG_INT64(0),
                 PG_GETARG_INT64(1),
                 PG_GETARG_INT64(2));
    opp_rating = PG_GETARG_INT64(3);
    opp_rd     = PG_GETARG_INT64(4);
    games      = PG_GETARG_INT64(5);
    sum_score  = PG_GETARG_INT64(6);
    tau        = PG_ARGISNULL(7) ? LAPLACE_GLICKO2_DEFAULT_TAU
                                 : PG_GETARG_INT64(7);

    if (games <= 0)
        ereport(ERROR,
            (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
             errmsg("laplace_glicko2_accumulate_games: games must be > 0 (got %ld)",
                    (long) games)));
    if (games > (INT64CONST(1) << 27))
        ereport(ERROR,
            (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
             errmsg("laplace_glicko2_accumulate_games: %ld games in one period "
                    "exceeds the per-relation bound", (long) games)));

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
             errmsg("function returning record called in context "
                    "that cannot accept type record")));
    BlessTupleDesc(tupdesc);

    /* Closed-form uniform fold: bit-identical to materializing `games`
     * observations of (opp_rating, opp_rd) with the same q/rem score split
     * and running glicko2_update_period, without the O(games) buffer. */
    glicko2_fold_uniform_period(&st, opp_rating, opp_rd,
                                games, sum_score, tau, 0);

    values[0] = Int64GetDatum(st.rating);
    values[1] = Int64GetDatum(st.rd);
    values[2] = Int64GetDatum(st.volatility);

    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}

/*
 * Shared validation + SPI-wrapped invocation for the flat batch-presence
 * primitives (entities_exist_bitmap, tier_batch_existence_probe). Both are
 * driven by the exact same underlying batch_presence_core() in
 * descent_probe.c (perfcache fast-path + one SPI batch query) -- a bit in
 * the returned bitmap is set iff that id was POSITIVELY confirmed to have a
 * committed entities row. Nothing here ever assumes presence by default.
 * The two SQL entry points exist separately (rather than one function under
 * two names) so ingest-descent call sites are self-documenting about *why*
 * they're calling it -- tier_batch_existence_probe denotes one round of the
 * C#-driven tier-by-tier trunk-to-leaf probe (TierTreeDescent.cs), while
 * entities_exist_bitmap is the general-purpose "do these ids exist" check.
 */
static Datum
presence_bitmap_datum(FunctionCallInfo fcinfo, const char* label,
                       int (*probe)(ArrayType*, uint8_t*, int))
{
    ArrayType*  ids_array;
    int         candidate_count;
    int         bitmap_bytes;
    bytea*      result;
    uint8*      bm;

    if (PG_ARGISNULL(0))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("%s: ids array must not be NULL", label)));

    ids_array = PG_GETARG_ARRAYTYPE_P(0);

    if (ARR_NDIM(ids_array) > 1)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("%s: ids array must be 1-dimensional", label)));

    if (ARR_ELEMTYPE(ids_array) != BYTEAOID)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("%s: ids array element type must be bytea", label)));

    if (ARR_HASNULL(ids_array))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("%s: ids array must not contain NULL", label)));

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
             errmsg("%s: SPI_connect failed", label)));

    {
        int rc = probe(ids_array, bm, candidate_count);

        SPI_finish();
        if (rc != SPI_OK_SELECT)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: bulk probe failed (rc=%d)", label, rc)));
    }

    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_entities_exist_bitmap);

Datum
pg_laplace_entities_exist_bitmap(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "entities_exist_bitmap",
                                  laplace_entities_present_bitmap);
}

PG_FUNCTION_INFO_V1(pg_laplace_tier_batch_existence_probe);

Datum
pg_laplace_tier_batch_existence_probe(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "tier_batch_existence_probe",
                                  laplace_tier_batch_existence_probe);
}

PG_FUNCTION_INFO_V1(pg_laplace_attestations_exist_bitmap);

/*
 * attestations_exist_bitmap(ids, type_ids, subject_ids) -- KEYED probe.
 * Attestations are partitioned LIST(type_id) -> HASH(subject_id); id alone
 * cannot prune, so the caller (which computed every id FROM these keys)
 * passes them alongside. Three parallel bytea[] arrays, validated for
 * shape/nulls/length parity here so descent_probe.c can assume alignment.
 */
Datum
pg_laplace_attestations_exist_bitmap(PG_FUNCTION_ARGS)
{
    const char *label = "attestations_exist_bitmap";
    ArrayType  *arrays[3];
    int         counts[3];
    int         a;
    int         candidate_count;
    int         bitmap_bytes;
    bytea      *result;
    uint8      *bm;

    for (a = 0; a < 3; a++)
    {
        if (PG_ARGISNULL(a))
            ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: argument %d must not be NULL", label, a + 1)));
        arrays[a] = PG_GETARG_ARRAYTYPE_P(a);
        if (ARR_NDIM(arrays[a]) > 1)
            ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("%s: argument %d must be 1-dimensional", label, a + 1)));
        if (ARR_ELEMTYPE(arrays[a]) != BYTEAOID)
            ereport(ERROR,
                (errcode(ERRCODE_DATATYPE_MISMATCH),
                 errmsg("%s: argument %d element type must be bytea", label, a + 1)));
        if (ARR_HASNULL(arrays[a]))
            ereport(ERROR,
                (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                 errmsg("%s: argument %d must not contain NULL", label, a + 1)));
        counts[a] = ARR_NDIM(arrays[a]) == 0
                    ? 0
                    : ArrayGetNItems(ARR_NDIM(arrays[a]), ARR_DIMS(arrays[a]));
    }
    if (counts[1] != counts[0] || counts[2] != counts[0])
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("%s: ids/type_ids/subject_ids must be the same length (%d/%d/%d)",
                    label, counts[0], counts[1], counts[2])));

    candidate_count = counts[0];
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
             errmsg("%s: SPI_connect failed", label)));

    {
        int rc = laplace_attestations_present_bitmap_keyed(
            arrays[0], arrays[1], arrays[2], bm, candidate_count);

        SPI_finish();
        if (rc != SPI_OK_SELECT)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("%s: bulk probe failed (rc=%d)", label, rc)));
    }

    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_entities_stored_bitmap);

Datum
pg_laplace_entities_stored_bitmap(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "entities_stored_bitmap",
                                  laplace_entities_stored_bitmap);
}

PG_FUNCTION_INFO_V1(pg_laplace_physicalities_exist_bitmap);

Datum
pg_laplace_physicalities_exist_bitmap(PG_FUNCTION_ARGS)
{
    return presence_bitmap_datum(fcinfo, "physicalities_exist_bitmap",
                                  laplace_physicalities_present_bitmap);
}

PG_FUNCTION_INFO_V1(pg_laplace_content_descent_bitmap);

/*
 * Legacy/back-compat entry point (content_descent_bitmap(ids, parents)).
 *
 * The real trunk-to-leaf, tier-by-tier descent algorithm is now driven
 * entirely by the C# orchestrator (TierTreeDescent.cs), which calls
 * tier_batch_existence_probe() once per tier/round with only the
 * candidates that round actually needs to check -- already filtered to
 * exclude descendants of nodes proven present in an earlier round, per the
 * content-addressing guarantee that a present node's whole subtree is
 * present too. This function is kept only so callers that still pass a
 * flat (ids, parents) pair in one shot keep working; `parents` is
 * validated for shape and otherwise UNUSED -- there is no tree-walk, no
 * default-present assumption, and no shortcircuiting here anymore. This
 * replaces the previous laplace_content_descent_bitmap_core()
 * implementation, which memset the whole bitmap to 0xFF ("assume every id
 * present") and only ever cleared bits it could positively disprove,
 * stopping descent at the first node it (correctly or incorrectly)
 * believed present -- that "assume-present, clear-on-disproof,
 * single-shortcircuit-per-branch" scheme is exactly the shape of bug that
 * independently existed on the C# side (TierTreeDescent.cs's previously
 * unconditional MarkProven() call); neither should ever assume presence
 * without a real, positive confirmation. Every bit returned here is set
 * iff a real batch presence query (or perfcache codepoint match) actually
 * confirmed that id has a committed entities row -- identical semantics to
 * tier_batch_existence_probe / entities_exist_bitmap, since all three
 * ultimately call the same batch_presence_core().
 */
Datum
pg_laplace_content_descent_bitmap(PG_FUNCTION_ARGS)
{
    ArrayType*  ids_array;
    ArrayType*  par_array;
    int         candidate_count;
    int         parent_count;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("content_descent_bitmap: ids and parents must not be NULL")));

    ids_array = PG_GETARG_ARRAYTYPE_P(0);
    par_array = PG_GETARG_ARRAYTYPE_P(1);

    if (ARR_NDIM(par_array) > 1)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("content_descent_bitmap: parents array must be 1-dimensional")));
    if (ARR_ELEMTYPE(par_array) != INT4OID)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("content_descent_bitmap: parents element type must be int4")));

    candidate_count = ARR_NDIM(ids_array) == 0
                      ? 0 : ArrayGetNItems(ARR_NDIM(ids_array), ARR_DIMS(ids_array));
    parent_count    = ARR_NDIM(par_array) == 0
                      ? 0 : ArrayGetNItems(ARR_NDIM(par_array), ARR_DIMS(par_array));
    if (candidate_count != parent_count)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("content_descent_bitmap: ids and parents length mismatch (%d vs %d)",
                    candidate_count, parent_count)));

    return presence_bitmap_datum(fcinfo, "content_descent_bitmap",
                                  laplace_tier_batch_existence_probe);
}

PG_FUNCTION_INFO_V1(pg_laplace_intent_preflight);

static const char*
present_ordinals_sql(const char* table)
{
    if (strcmp(table, "entities") == 0)
        return "SELECT idx FROM laplace.entities_present_ordinals($1)";
    if (strcmp(table, "physicalities") == 0)
        return "SELECT idx FROM laplace.physicalities_present_ordinals($1)";
    if (strcmp(table, "attestations") == 0)
        return "SELECT idx FROM laplace.attestations_present_ordinals($1)";
    ereport(ERROR,
        (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
         errmsg("intent_preflight: unknown table \"%s\"", table)));
    return NULL;
}

static bytea*
build_exist_bitmap(ArrayType* ids_array, const char* table)
{
    int         candidate_count;
    int         bitmap_bytes;
    bytea*      result;
    uint8*      bm;
    Oid         argtypes[1];
    Datum       args[1];
    int         spi_rc;
    uint64      i;
    const char* sql;

    if (ARR_NDIM(ids_array) > 1)
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
             errmsg("intent_preflight: ids array must be 1-dimensional")));

    if (ARR_ELEMTYPE(ids_array) != BYTEAOID)
        ereport(ERROR,
            (errcode(ERRCODE_DATATYPE_MISMATCH),
             errmsg("intent_preflight: ids array element type must be bytea")));

    if (ARR_HASNULL(ids_array))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("intent_preflight: ids array must not contain NULL")));

    candidate_count = ARR_NDIM(ids_array) == 0
                      ? 0
                      : ArrayGetNItems(ARR_NDIM(ids_array), ARR_DIMS(ids_array));

    if (candidate_count <= 0)
    {
        result = (bytea*) palloc(VARHDRSZ);
        SET_VARSIZE(result, VARHDRSZ);
        return result;
    }

    if (candidate_count > 250000)
        ereport(ERROR,
            (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
             errmsg("intent_preflight: id batch too large (%d > 250000)", candidate_count)));

    bitmap_bytes = (candidate_count + 7) / 8;
    result = (bytea*) palloc(VARHDRSZ + bitmap_bytes);
    SET_VARSIZE(result, VARHDRSZ + bitmap_bytes);
    memset(VARDATA(result), 0, bitmap_bytes);
    bm = (uint8*) VARDATA(result);

    if (SPI_connect() != SPI_OK_CONNECT)
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("intent_preflight: SPI_connect failed")));

    argtypes[0] = BYTEAARRAYOID;
    args[0]     = PointerGetDatum(ids_array);
    sql         = present_ordinals_sql(table);

    spi_rc = SPI_execute_with_args(sql, 1, argtypes, args, NULL, true, 0);
    if (spi_rc != SPI_OK_SELECT)
    {
        SPI_finish();
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("intent_preflight: SPI query on %s failed (rc=%d)", table, spi_rc)));
    }

    for (i = 0; i < SPI_processed; i++)
    {
        bool  isnull;
        Datum d = SPI_getbinval(SPI_tuptable->vals[i],
                                SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull)
        {
            int pos = DatumGetInt32(d);
            if (pos >= 0 && pos < candidate_count)
                bm[pos >> 3] |= (uint8)(1u << (pos & 7u));
        }
    }

    SPI_finish();
    return result;
}

Datum
pg_laplace_intent_preflight(PG_FUNCTION_ARGS)
{
    ArrayType* ent_array;
    ArrayType* phys_array;
    ArrayType* att_array;
    TupleDesc  tupdesc;
    Datum      values[3];
    bool       nulls[3] = {false, false, false};
    HeapTuple  tuple;
    bytea*     ent_bm;
    bytea*     phys_bm;
    bytea*     att_bm;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1) || PG_ARGISNULL(2))
        ereport(ERROR,
            (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
             errmsg("intent_preflight: all id arrays must be non-NULL")));

    ent_array   = PG_GETARG_ARRAYTYPE_P(0);
    phys_array  = PG_GETARG_ARRAYTYPE_P(1);
    att_array   = PG_GETARG_ARRAYTYPE_P(2);

    ent_bm  = build_exist_bitmap(ent_array,  "entities");
    phys_bm = build_exist_bitmap(phys_array, "physicalities");
    att_bm  = build_exist_bitmap(att_array,  "attestations");

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
             errmsg("intent_preflight: return type must be a composite type")));

    BlessTupleDesc(tupdesc);

    values[0] = PointerGetDatum(ent_bm);
    values[1] = PointerGetDatum(phys_bm);
    values[2] = PointerGetDatum(att_bm);
    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}

PG_FUNCTION_INFO_V1(pg_relation_type_resolve);

Datum
pg_relation_type_resolve(PG_FUNCTION_ARGS)
{
    text*   surface_txt = PG_GETARG_TEXT_PP(0);
    char*   surface     = text_to_cstring(surface_txt);
    hash128_t type_id;
    int     rc = laplace_relation_resolve_surface(surface, &type_id, NULL, NULL, NULL, NULL);
    pfree(surface);
    if (rc < 0)
        PG_RETURN_NULL();
    PG_RETURN_DATUM(hash128_to_datum(&type_id));
}

PG_FUNCTION_INFO_V1(pg_relation_type_in_family);

Datum
pg_relation_type_in_family(PG_FUNCTION_ARGS)
{
    bytea*  type_ba = PG_GETARG_BYTEA_PP(0);
    text*   fam_txt = PG_GETARG_TEXT_PP(1);
    hash128_t type_id = datum_to_hash128(PointerGetDatum(type_ba));
    char*   family  = text_to_cstring(fam_txt);
    int     in_family = 0;
    int     rc = laplace_relation_in_family(&type_id, family, &in_family);
    pfree(family);
    if (rc != 0)
        PG_RETURN_BOOL(false);
    PG_RETURN_BOOL(in_family != 0);
}




PG_FUNCTION_INFO_V1(pg_relation_rank);

Datum
pg_relation_rank(PG_FUNCTION_ARGS)
{
    bytea*    type_ba = PG_GETARG_BYTEA_PP(0);
    hash128_t type_id = datum_to_hash128(PointerGetDatum(type_ba));
    const laplace_relation_def_t* def = NULL;
    int       rc = laplace_relation_lookup(&type_id, &def);
    if (rc != 0 || def == NULL)
        PG_RETURN_NULL();
    PG_RETURN_FLOAT8(def->rank);
}








PG_FUNCTION_INFO_V1(pg_relation_rank_resolved);

Datum
pg_relation_rank_resolved(PG_FUNCTION_ARGS)
{
    bytea*    type_ba = PG_GETARG_BYTEA_PP(0);
    hash128_t cur_id  = datum_to_hash128(PointerGetDatum(type_ba));
    const laplace_relation_def_t* def = NULL;

    
    if (laplace_relation_lookup(&cur_id, &def) == 0 && def != NULL)
        PG_RETURN_FLOAT8(def->rank);

    if (SPI_connect() != SPI_OK_CONNECT)
        PG_RETURN_NULL();

    double out_rank = 0.0;
    bool   found    = false;
    for (int hop = 0; hop < 8 && !found; hop++)
    {
        Oid    argtypes[1]  = { BYTEAOID };
        Datum  args[1]      = { hash128_to_datum(&cur_id) };
        bool   isnull;
        int    rc = SPI_execute_with_args(
            "SELECT object_id FROM laplace.consensus "
            "WHERE subject_id = $1 "
            "  AND type_id = laplace.relation_type_id('IS_A') "
            "ORDER BY laplace.eff_mu(rating, rd) DESC LIMIT 1",
            1, argtypes, args, NULL, true, 1);
        if (rc != SPI_OK_SELECT || SPI_processed == 0)
            break;

        Datum pd = SPI_getbinval(SPI_tuptable->vals[0],
                                 SPI_tuptable->tupdesc, 1, &isnull);
        if (isnull)
            break;

        hash128_t parent_id = datum_to_hash128(pd);
        const laplace_relation_def_t* pdef = NULL;
        if (laplace_relation_lookup(&parent_id, &pdef) == 0 && pdef != NULL)
        {
            out_rank = pdef->rank;   
            found    = true;
        }
        else
        {
            cur_id = parent_id;      
        }
    }

    SPI_finish();
    if (found)
        PG_RETURN_FLOAT8(out_rank);
    PG_RETURN_NULL();
}

PG_FUNCTION_INFO_V1(pg_relation_canonical);

Datum
pg_relation_canonical(PG_FUNCTION_ARGS)
{
    bytea*    type_ba = PG_GETARG_BYTEA_PP(0);
    hash128_t type_id = datum_to_hash128(PointerGetDatum(type_ba));
    const laplace_relation_def_t* def = NULL;
    int       rc = laplace_relation_lookup(&type_id, &def);
    if (rc != 0 || def == NULL || def->canonical == NULL)
        PG_RETURN_NULL();
    PG_RETURN_TEXT_P(cstring_to_text(def->canonical));
}

void _PG_init(void);
void
_PG_init(void)
{
    




    laplace_corpus_guc_init();
    laplace_substrate_perfcache_init();
    laplace_substrate_perfcache_prewarm();

    {
        const int rc = laplace_runtime_init(LAPLACE_RUNTIME_HOST_PG,
                                            laplace_substrate_native_mkl_threads());
        if (rc == -2)
            ereport(FATAL,
                    (errmsg("laplace_substrate: MKL required but unavailable (laplace_runtime_init rc=-2)"),
                     errhint("Deploy laplace_dynamics with MKL; check dynamic_library_path.")));
        if (rc != 0)
            ereport(FATAL,
                    (errmsg("laplace_substrate: laplace_runtime_init failed (rc=%d)", rc),
                     errhint("Verify oneAPI MKL CBWR / AVX2 build flags.")));
    }
}
