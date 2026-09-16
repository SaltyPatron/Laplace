#include "postgres.h"
#include "miscadmin.h"
#include "access/detoast.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/array.h"
#include "utils/tuplestore.h"
#include "utils/memutils.h"
#include "utils/snapmgr.h"
#include "utils/timestamp.h"
#include "physicality_descriptor_admission_pg.h"
#include "generated_stage_sink.h"

#include "laplace/core/mantissa.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/math4d.h"
#include "laplace/core/trajectory.h"
#include "laplace/core/sql_catalog.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"

PG_FUNCTION_INFO_V1(pg_laplace_session_turn_ids);
PG_FUNCTION_INFO_V1(pg_laplace_session_append_turns);

static SPIPlanPtr session_manifest_plan = NULL;
static SPIPlanPtr session_manifest_metadata_plan = NULL;
static SPIPlanPtr session_lock_plan = NULL;
static SPIPlanPtr session_coords_plan = NULL;
static SPIPlanPtr session_write_plan = NULL;

/* A stable session handle owns a mutable projection of its ordered turns.
 * Each turn retains its canonical Content physicality through governed apply.
 * The session handle is not the content hash of the changing turn sequence. */
enum { SESSION_MANIFEST_TYPE = 3 };

static SPIPlanPtr
session_plan(SPIPlanPtr *cached, const char *query, int nargs, Oid *types)
{
    if (*cached == NULL)
    {
        SPIPlanPtr plan = SPI_prepare(query, nargs, types);
        if (plan == NULL || SPI_keepplan(plan) != 0)
            elog(ERROR, "conversation session: preparing typed operation failed");
        *cached = plan;
    }
    return *cached;
}

static SPIPlanPtr
session_manifest(void)
{
    Oid types[2] = {BYTEAOID, BYTEAOID};
    return session_plan(&session_manifest_plan,
        laplace_sql_query_text("conversation.manifest"), 2, types);
}

static void
require_projection_manifest(void)
{
    for (uint64 i = 0; i < SPI_processed; ++i)
    {
        bool isnull;
        Datum type = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 2, &isnull);
        if (isnull || DatumGetInt16(type) != SESSION_MANIFEST_TYPE)
            ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION),
                errmsg("conversation session: legacy Content manifest requires typed recovery"),
                errhint("Preserve and classify the existing session manifest before migrating it to Projection; append does not rewrite legacy content.")));
    }
}

typedef struct SessionTurnOutput
{
    ReturnSetInfo *result;
    int32 limit;
    bool limit_reached;
} SessionTurnOutput;

static int
emit_session_turn(void *context, size_t ordinal, const hash128_t *id, uint64 flags)
{
    SessionTurnOutput *output = context;
    (void) flags;
    if (ordinal > (size_t) output->limit)
    {
        output->limit_reached = true;
        return 1;
    }
    CHECK_FOR_INTERRUPTS();
    Datum values[2] = {Int32GetDatum((int32) ordinal), hash128_to_datum(id)};
    bool nulls[2] = {false, false};
    tuplestore_putvalues(output->result->setResult, output->result->setDesc, values, nulls);
    pfree(DatumGetPointer(values[1]));
    output->limit_reached = ordinal == (size_t) output->limit;
    return output->limit_reached ? 1 : 0;
}

/* Read the session's persisted order authority once. Topic summaries and
 * per-process transcripts cannot supply or reorder these occurrences. */
Datum
pg_laplace_session_turn_ids(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea *session;
    int32 limit = PG_ARGISNULL(1) ? PG_INT32_MAX : PG_GETARG_INT32(1);
    bool spi_top = false;

    InitMaterializedSRF(fcinfo, 0);
    if (limit < 0)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                       errmsg("session_turn_ids: limit must not be negative")));
    if (PG_ARGISNULL(0) || limit == 0)
        return (Datum) 0;
    session = PG_GETARG_BYTEA_PP(0);
    if (VARSIZE_ANY_EXHDR(session) != sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                       errmsg("session_turn_ids: session id must contain 16 bytes")));

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "session_turn_ids: SPI_connect failed");
    hash128_t session_id, physicality_id, legacy_id;
    memcpy(&session_id, VARDATA_ANY(session), sizeof(session_id));
    laplace_physicality_id_compute(session_id, SESSION_MANIFEST_TYPE, &physicality_id);
    laplace_physicality_id_compute(session_id, 1, &legacy_id);
    Datum args[2] = {hash128_to_datum(&physicality_id), hash128_to_datum(&legacy_id)};
    if (SPI_execute_plan(session_manifest(), args, NULL, true, 2) != SPI_OK_SELECT)
        elog(ERROR, "session_turn_ids: reading session manifest failed");
    require_projection_manifest();

    if (SPI_processed == 1)
    {
        bool isnull;
        Datum datum = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull)
        {
            uint32 count;
            const unsigned char *points = laplace_trajectory_wkb_points(DatumGetByteaPP(datum), &count);
            double *vertices = palloc((Size) count * 4 * sizeof(double));
            memcpy(vertices, points, (Size) count * 4 * sizeof(double));
            SessionTurnOutput output = {result, limit, false};
            int rc = trajectory_visit_constituents(vertices, count, emit_session_turn, &output);
            if (rc < 0 && !output.limit_reached)
                elog(ERROR, "session_turn_ids: invalid session trajectory");
            pfree(vertices);
        }
    }
    if (SPI_tuptable != NULL) SPI_freetuptable(SPI_tuptable);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

/* Projection bodies are observations made by the native session operator.
 * The ordinary fixed source has the repository's explicit AppDerived prior;
 * a tenant, prompt or response identity does not imply this derivation prior. */
#define SESSION_PROJECTION_PRIOR 0.40
/* Seven outer set executions plus at most seven initial plan preparations. */
#define SESSION_OPERATION_RESERVATION 14

typedef struct SessionAdmission {
    MemoryContext context;
    MemoryContextCallback cleanup;
    size_t maximum_bytes, bytes, peak_bytes;
    intent_stage_t *raw[2], *declaration;
    size_t observation_count;
    hash128_t source, unit;
} SessionAdmission;

static void session_charge(SessionAdmission *state, size_t bytes)
{
    if (bytes > state->maximum_bytes - state->bytes)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: physicality byte grant exhausted")));
    state->bytes += bytes;
    if (state->bytes > state->peak_bytes) state->peak_bytes = state->bytes;
}

static void *session_alloc(SessionAdmission *state, size_t bytes)
{
    if (bytes > MaxAllocSize)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: allocation exceeds PostgreSQL datum capacity")));
    session_charge(state, bytes);
    return MemoryContextAllocZero(state->context, bytes);
}

static void session_release(SessionAdmission *state, void *pointer, size_t bytes)
{
    if (pointer != NULL) { pfree(pointer); state->bytes -= bytes; }
}

/* Statement disposition, not a commit claim. Append observes at most its
 * previous and replacement body; every missing identifier remains explicit. */
static char *session_view_receipt(SessionAdmission *state,
    const laplace_physicality_pg_admission_result *result, size_t *out_bytes)
{
    size_t unavailable = 0;
    *out_bytes = 0;
    for (size_t i = 0; i < result->form_count; ++i)
        if (result->forms[i].view_state == PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE) ++unavailable;
    if (!unavailable) return NULL;
    if (result->form_count > 2 || result->view_missing_count > (SIZE_MAX - 512) / 35)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session view receipt exceeds its finite extent")));
    size_t capacity = 512 + result->view_missing_count * 35;
    char *buffer = session_alloc(state, capacity);
    size_t used = 0;
#define SESSION_RECEIPT_APPEND(...) do { \
    int written = snprintf(buffer + used, capacity - used, __VA_ARGS__); \
    if (written < 0 || (size_t) written >= capacity - used) \
        elog(ERROR, "session view receipt reservation is insufficient"); \
    used += (size_t) written; \
} while (0)
    SESSION_RECEIPT_APPEND("{\"schema\":\"laplace.session-descriptor-views/v1\",\"transaction_pending\":true,\"forms\":[");
    size_t emitted = 0;
    for (size_t i = 0; i < result->form_count; ++i)
    {
        const physicality_descriptor_admitted_form_t *form = &result->forms[i];
        if (form->view_state != PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE) continue;
        char id[33];
        hex_encode((const char *) &form->descriptor_id, 16, id);
        id[32] = 0;
        SESSION_RECEIPT_APPEND("%s{\"descriptor_id\":\"%s\",\"view_state\":1,\"missing_first\":%zu,\"missing_count\":%zu}",
            emitted++ ? "," : "", id, form->missing_first, form->missing_count);
    }
    SESSION_RECEIPT_APPEND("],\"missing_ids\":[");
    for (size_t i = 0; i < result->view_missing_count; ++i)
    {
        char id[33];
        hex_encode((const char *) &result->view_missing_ids[i], 16, id);
        id[32] = 0;
        SESSION_RECEIPT_APPEND("%s\"%s\"", i ? "," : "", id);
    }
    SESSION_RECEIPT_APPEND("]}");
#undef SESSION_RECEIPT_APPEND
    *out_bytes = capacity;
    return buffer;
}

static void session_cleanup(void *arg)
{
    SessionAdmission *state = arg;
    for (size_t i = 0; i < 2; ++i) { intent_stage_free(state->raw[i]); state->raw[i] = NULL; }
    intent_stage_free(state->declaration); state->declaration = NULL;
}

static int64 session_unix_us(TimestampTz timestamp)
{
    if (TIMESTAMP_NOT_FINITE(timestamp) || timestamp > INT64_MAX - INTENT_STAGE_PG_EPOCH_UNIX_US)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
            errmsg("session_append_turns: observation timestamp must be finite")));
    return timestamp + INTENT_STAGE_PG_EPOCH_UNIX_US;
}

static void session_observe(SessionAdmission *state, const hash128_t *placement,
    const hash128_t *entity, const double coord[4], const hilbert128_t *hilbert,
    const double *trajectory, size_t vertices, int32 constituents,
    bool alignment_null, double alignment, bool dimension_null, int32 dimension,
    int64 observed)
{
    if (state->observation_count >= 2)
        elog(ERROR, "session_append_turns: more than two projection observations");
    intent_stage_t **slot = &state->raw[state->observation_count];
    *slot = intent_stage_new_bounded(0, state->maximum_bytes - state->bytes);
    if (*slot == NULL)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: projection stage exceeds byte grant")));
    int status = intent_stage_add_physicality(*slot, placement, entity,
        SESSION_MANIFEST_TYPE, coord, hilbert, trajectory, vertices, constituents,
        alignment_null, alignment, dimension_null, dimension, observed);
    if (status != 0)
        ereport(ERROR, (errcode(intent_stage_allocation_failed(*slot) ?
            ERRCODE_PROGRAM_LIMIT_EXCEEDED : ERRCODE_DATA_EXCEPTION),
            errmsg("session_append_turns: invalid or unbudgeted projection observation")));
    session_charge(state, intent_stage_memory_peak_bytes(*slot));
    state->bytes -= intent_stage_memory_peak_bytes(*slot);
    session_charge(state, intent_stage_memory_bytes(*slot));
    ++state->observation_count;
}

typedef struct SessionMembers
{
    hash128_t *ids;
    uint64 *flags;
} SessionMembers;

static int
collect_session_turn(void *context, size_t ordinal, const hash128_t *id, uint64 flags)
{
    SessionMembers *members = context;
    CHECK_FOR_INTERRUPTS();
    members->ids[ordinal - 1] = *id;
    members->flags[ordinal - 1] = flags;
    return 0;
}

/* Invoked by the canonical writer inside the same journaled transaction as
 * turn evidence and consensus. The shared database apply lock precedes the
 * session row lock; no process-local transcript supplies the persisted order. */
Datum
pg_laplace_session_append_turns(PG_FUNCTION_ARGS)
{
    bytea *session;
    ArrayType *turn_array;
    size_t turn_input_bytes = toast_raw_datum_size(PG_GETARG_DATUM(1));
    Datum *turns;
    bool *nulls;
    int added;
    bool spi_top = false;
    hash128_t session_id, physicality_id, legacy_id;
    size_t previous_count = 0;
    uint32 previous_vertices = 0;
    double *previous = NULL;
    SessionAdmission *admission = NULL;
    int64 observed_unix = session_unix_us(PG_GETARG_TIMESTAMPTZ(2));
    int64 requested_bytes = PG_NARGS() == 7 ? PG_GETARG_INT64(4) :
        (int64) maintenance_work_mem * 1024;
    int32 maximum_operations = PG_NARGS() == 7 ? PG_GETARG_INT32(5) : 512;
    int64 maximum_logical = PG_NARGS() == 7 ? PG_GETARG_INT64(6) : requested_bytes / (int64) sizeof(hash128_t);
    if (requested_bytes <= 0 || (uint64) requested_bytes > SIZE_MAX ||
        maximum_operations <= SESSION_OPERATION_RESERVATION || maximum_logical <= 0 || (uint64) maximum_logical > SIZE_MAX)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
            errmsg("session_append_turns: positive finite byte, operation and logical-work grants are required")));
    if (toast_raw_datum_size(PG_GETARG_DATUM(0)) != VARHDRSZ + sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
            errmsg("session_append_turns: session id must contain exactly 16 bytes")));
    if (PG_NARGS() == 7 && toast_raw_datum_size(PG_GETARG_DATUM(3)) != VARHDRSZ + sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
            errmsg("session_append_turns: source unit must contain exactly 16 bytes")));

    const size_t fixed_payload = sizeof(SessionAdmission) + 4096;
    if ((size_t) requested_bytes < fixed_payload ||
        turn_input_bytes > (size_t) requested_bytes - fixed_payload)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: source turn payload exceeds byte grant")));
    session = PG_GETARG_BYTEA_PP(0);
    turn_array = PG_GETARG_ARRAYTYPE_P(1);
    if (VARSIZE_ANY_EXHDR(session) != sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                       errmsg("session_append_turns: session id must contain 16 bytes")));
    if (ARR_NDIM(turn_array) > 1 || ARR_ELEMTYPE(turn_array) != BYTEAOID)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                       errmsg("session_append_turns: turns must be a one-dimensional bytea array")));
    added = ArrayGetNItems(ARR_NDIM(turn_array), ARR_DIMS(turn_array));
    if ((size_t) added > ((size_t) requested_bytes - fixed_payload - turn_input_bytes)
        / (sizeof(Datum) + sizeof(bool)))
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: source turn array exceeds byte grant")));
    deconstruct_array(turn_array, BYTEAOID, -1, false, TYPALIGN_INT, &turns, &nulls, &added);
    if (added == 0) PG_RETURN_INT32(0);
    for (int i = 0; i < added; ++i)
        if (nulls[i] || VARSIZE_ANY_EXHDR(DatumGetByteaPP(turns[i])) != sizeof(hash128_t))
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                           errmsg("session_append_turns: each turn must contain 16 bytes")));
    memcpy(&session_id, VARDATA_ANY(session), sizeof(session_id));
    laplace_physicality_id_compute(session_id, SESSION_MANIFEST_TYPE, &physicality_id);
    laplace_physicality_id_compute(session_id, 1, &legacy_id);

    /* Shared apply lock comes before the session row lock. A statement that
     * waited for either lock must not retain its pre-wait provider snapshot. */
    laplace_generated_stage_sink_lock();
    admission = palloc0(sizeof(*admission));
    admission->context = CurrentMemoryContext;
    admission->maximum_bytes = (size_t) requested_bytes;
    admission->cleanup.func = session_cleanup;
    admission->cleanup.arg = admission;
    MemoryContextRegisterResetCallback(admission->context, &admission->cleanup);
    session_charge(admission, sizeof(*admission) + turn_input_bytes + 4096
        + (size_t) added * (sizeof(Datum) + sizeof(bool)));
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "session_append_turns: SPI_connect failed");
    Oid id_types[1] = {BYTEAOID};
    Datum args[1] = {PointerGetDatum(session)};
    SPIPlanPtr lock = session_plan(&session_lock_plan,
        laplace_sql_query_text("conversation.lock"), 1, id_types);
    if (SPI_execute_plan(lock, args, NULL, false, 1) != SPI_OK_SELECT || SPI_processed != 1)
        elog(ERROR, "session_append_turns: session entity must be admitted before its turns");
    SPI_freetuptable(SPI_tuptable);

    PushActiveSnapshot(GetLatestSnapshot());
    Datum manifest_args[2] = {hash128_to_datum(&physicality_id), hash128_to_datum(&legacy_id)};
    Oid metadata_types[1] = {BYTEAARRAYOID};
    Datum metadata_args[1] = {PointerGetDatum(construct_array(manifest_args, 2,
        BYTEAOID, -1, false, TYPALIGN_INT))};
    SPIPlanPtr metadata = session_plan(&session_manifest_metadata_plan,
        laplace_sql_query_text("ingest.physicality_descriptor_provider_metadata"), 1, metadata_types);
    if (SPI_execute_snapshot(metadata, metadata_args, NULL, GetActiveSnapshot(), InvalidSnapshot,
        true, false, 2) != SPI_OK_SELECT)
        elog(ERROR, "session_append_turns: reading prior projection metadata failed");
    size_t manifest_payload = 0;
    for (uint64 i = 0; i < SPI_processed; ++i) {
        HeapTuple row = SPI_tuptable->vals[i];
        TupleDesc desc = SPI_tuptable->tupdesc;
        bool missing;
        Datum type = SPI_getbinval(row, desc, 3, &missing);
        if (missing || DatumGetInt16(type) != SESSION_MANIFEST_TYPE)
            ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION),
                errmsg("conversation session: legacy Content manifest requires typed recovery")));
        Datum entity = SPI_getbinval(row, desc, 2, &missing);
        if (missing || toast_raw_datum_size(entity) != VARHDRSZ + sizeof(session_id) ||
            memcmp(VARDATA_ANY(DatumGetByteaPP(entity)), &session_id, sizeof(session_id)) != 0)
            elog(ERROR, "session_append_turns: prior projection has an invalid entity owner");
        /* Inspect external/compressed TOAST sizes before asking PostGIS for
         * expanded WKB. Two copies cover decompression plus its set projection;
         * row storage and exact aligned/native copies are charged separately. */
        session_charge(admission, row->t_len);
        manifest_payload += row->t_len;
        for (int column = 4; column <= 6; column += 2) {
            Datum geometry = SPI_getbinval(row, desc, column, &missing);
            if (missing) {
                if (column == 4) elog(ERROR, "session_append_turns: prior coordinate is missing");
                continue;
            }
            size_t raw = toast_raw_datum_size(geometry);
            if (raw > (admission->maximum_bytes - admission->bytes) / 2)
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                    errmsg("session_append_turns: prior projection exceeds byte grant")));
            session_charge(admission, raw * 2);
            manifest_payload += raw * 2;
        }
    }
    SPI_freetuptable(SPI_tuptable);
    if (SPI_execute_snapshot(session_manifest(), manifest_args, NULL, GetActiveSnapshot(), InvalidSnapshot,
        true, false, 2) != SPI_OK_SELECT)
        elog(ERROR, "session_append_turns: reading session manifest failed");
    require_projection_manifest();
    if (SPI_processed == 1)
    {
        bool isnull;
        Datum value = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull)
        {
            const unsigned char *points = laplace_trajectory_wkb_points(
                DatumGetByteaPP(value), &previous_vertices);
            previous = session_alloc(admission, (Size) previous_vertices * 4 * sizeof(double));
            memcpy(previous, points, (Size) previous_vertices * 4 * sizeof(double));
            if (trajectory_constituent_count(previous, previous_vertices, &previous_count) != 0)
                elog(ERROR, "session_append_turns: invalid prior manifest");
        }
        /* Capture the complete persisted old form, including nullable fields,
         * before its stable projection address receives the next body. */
        HeapTuple old_row = SPI_tuptable->vals[0];
        TupleDesc old_desc = SPI_tuptable->tupdesc;
        double old_coord[4], old_alignment = 0;
        int32 old_dimension = 0;
        bool alignment_null, dimension_null;
        hilbert128_t old_hilbert;
        for (int axis = 0; axis < 4; ++axis) {
            value = SPI_getbinval(old_row, old_desc, axis + 3, &isnull);
            if (isnull) elog(ERROR, "session_append_turns: prior coordinate is incomplete");
            old_coord[axis] = DatumGetFloat8(value);
        }
        value = SPI_getbinval(old_row, old_desc, 7, &isnull);
        if (isnull || VARSIZE_ANY_EXHDR(DatumGetByteaPP(value)) != sizeof(old_hilbert))
            elog(ERROR, "session_append_turns: prior Hilbert identity is invalid");
        memcpy(&old_hilbert, VARDATA_ANY(DatumGetByteaPP(value)), sizeof(old_hilbert));
        value = SPI_getbinval(old_row, old_desc, 8, &isnull);
        if (isnull || DatumGetInt32(value) < 0 || (size_t) DatumGetInt32(value) != previous_count)
            elog(ERROR, "session_append_turns: prior count disagrees with its exact trajectory");
        value = SPI_getbinval(old_row, old_desc, 9, &alignment_null);
        if (!alignment_null) old_alignment = DatumGetFloat8(value);
        value = SPI_getbinval(old_row, old_desc, 10, &dimension_null);
        if (!dimension_null) old_dimension = DatumGetInt32(value);
        value = SPI_getbinval(old_row, old_desc, 11, &isnull);
        if (isnull) elog(ERROR, "session_append_turns: prior observation time is missing");
        session_observe(admission, &physicality_id, &session_id, old_coord,
            &old_hilbert, previous, previous_vertices, (int32) previous_count,
            alignment_null, old_alignment, dimension_null, old_dimension,
            session_unix_us(DatumGetTimestampTz(value)));
    }
    SPI_freetuptable(SPI_tuptable);

    admission->bytes -= manifest_payload;
    if (previous_count > PG_INT32_MAX - (size_t) added ||
        previous_count + added > (MaxAllocSize - 9 - VARHDRSZ) / (4 * sizeof(double)))
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("session_append_turns: session manifest exceeds allocation capacity")));
    size_t total = previous_count + added;
    if (previous_count > (size_t) maximum_logical || total > (size_t) maximum_logical - previous_count)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: turn sequence exceeds logical-work grant")));
    size_t composition_work = previous_count + total;
    SessionMembers members = {session_alloc(admission, total * sizeof(hash128_t)),
        session_alloc(admission, total * sizeof(uint64))};
    if (previous && trajectory_visit_constituents(previous, previous_vertices,
                                                   collect_session_turn, &members) != 0)
        elog(ERROR, "session_append_turns: invalid prior turn sequence");
    session_release(admission, previous, (size_t) previous_vertices * 4 * sizeof(double));
    for (int i = 0; i < added; ++i)
        memcpy(&members.ids[previous_count + i], VARDATA_ANY(DatumGetByteaPP(turns[i])), sizeof(hash128_t));

    Datum *ids = session_alloc(admission, total * sizeof(Datum));
    /* Scalar varlenas, their array copy and one bounded coordinate set result. */
    session_charge(admission, total * (2 * INTALIGN(VARHDRSZ + sizeof(hash128_t))
        + sizeof(HeapTupleData) + sizeof(ItemPointerData) + 128) + ARR_OVERHEAD_NONULLS(1));
    for (size_t i = 0; i < total; ++i) ids[i] = hash128_to_datum(&members.ids[i]);
    ArrayType *all_turns = construct_array(ids, (int) total, BYTEAOID, -1, false, TYPALIGN_INT);
    Oid coord_types[1] = {BYTEAARRAYOID};
    Datum coord_args[1] = {PointerGetDatum(all_turns)};
    SPIPlanPtr coordinates = session_plan(&session_coords_plan,
        laplace_sql_query_text("conversation.coordinates"), 1, coord_types);
    if (SPI_execute_snapshot(coordinates, coord_args, NULL, GetActiveSnapshot(), InvalidSnapshot,
        true, false, 0) != SPI_OK_SELECT || SPI_processed != total)
        elog(ERROR, "session_append_turns: each turn requires an admitted content placement");
    double *coords = session_alloc(admission, total * 4 * sizeof(double));
    for (size_t i = 0; i < total; ++i)
    {
        bool isnull;
        HeapTuple row = SPI_tuptable->vals[i];
        TupleDesc desc = SPI_tuptable->tupdesc;
        int16 tier = DatumGetInt16(SPI_getbinval(row, desc, 2, &isnull));
        if (isnull || tier < 0 || tier > UINT8_MAX)
            elog(ERROR, "session_append_turns: invalid turn floor");
        /* Batch projections may predate contextual flags. Populate absent flags
         * from this same set read; retain any existing typed occurrence payload. */
        if (i >= previous_count || members.flags[i] == 0)
            members.flags[i] = laplace_vertex_flags((uint8) tier, false, 0);
        for (int axis = 0; axis < 4; ++axis)
        {
            Datum coordinate = SPI_getbinval(row, desc, axis + 3, &isnull);
            if (isnull) elog(ERROR, "session_append_turns: incomplete turn placement");
            coords[i * 4 + axis] = DatumGetFloat8(coordinate);
        }
    }
    SPI_freetuptable(SPI_tuptable);
    hilbert128_t hilbert;
    double centroid[4];
    size_t centroid_bytes = 0;
    if (math4d_centroid_workspace_size(total, &centroid_bytes) != 0)
        elog(ERROR, "session_append_turns: centroid workspace size overflow");
    void *centroid_workspace = session_alloc(admission, centroid_bytes);
    if (math4d_centroid_with_workspace(coords, total, centroid_workspace, centroid_bytes, centroid) != 0)
        elog(ERROR, "session_append_turns: canonical centroid failed");
    session_release(admission, centroid_workspace, centroid_bytes);
    session_release(admission, coords, total * 4 * sizeof(double));
    hilbert4d_encode(centroid, &hilbert);
    double *packed = session_alloc(admission, total * 4 * sizeof(double));
    size_t packed_count;
    if (trajectory_build_flagged_rle(members.ids, members.flags, total, packed, &packed_count) != 0)
        elog(ERROR, "session_append_turns: composing session trajectory failed");
    session_release(admission, members.ids, total * sizeof(hash128_t));
    session_release(admission, members.flags, total * sizeof(uint64));
    uint32 geometry_type = packed_count == 1 ? 3001 : 3002;
    Size header = packed_count == 1 ? 5 : 9;
    bytea *wkb = session_alloc(admission, VARHDRSZ + header + packed_count * 4 * sizeof(double));
    SET_VARSIZE(wkb, VARHDRSZ + header + packed_count * 4 * sizeof(double));
    unsigned char *bytes = (unsigned char *) VARDATA(wkb);
    bytes[0] = 1;
    memcpy(bytes + 1, &geometry_type, 4);
    if (packed_count != 1)
    {
        uint32 vertices = (uint32) packed_count;
        memcpy(bytes + 5, &vertices, 4);
    }
    memcpy(bytes + header, packed, packed_count * 4 * sizeof(double));
    session_observe(admission, &physicality_id, &session_id, centroid, &hilbert,
        packed, packed_count, (int32) total, true, 0, true, 0, observed_unix);
    session_release(admission, packed, total * 4 * sizeof(double));
    if (PG_NARGS() == 7)
        memcpy(&admission->unit, VARDATA_ANY(PG_GETARG_BYTEA_PP(3)), sizeof(admission->unit));
    else if (intent_stage_semantic_digest_batch((const intent_stage_t *const *)admission->raw,
        admission->observation_count, &admission->unit) != 0)
        elog(ERROR, "session_append_turns: deriving exact operation receipt failed");
    size_t source_peak = 0;
    physicality_descriptor_status_t source_status = physicality_descriptor_session_source_create(
        admission->maximum_bytes - admission->bytes, &admission->source,
        &admission->declaration, &source_peak);
    if (source_status != PHYSICALITY_DESCRIPTOR_OK)
        ereport(ERROR, (errcode(source_status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED ?
            ERRCODE_PROGRAM_LIMIT_EXCEEDED : ERRCODE_DATA_EXCEPTION),
            errmsg("session_append_turns: ordinary projection source creation failed (%d)", (int)source_status)));
    session_charge(admission, source_peak); admission->bytes -= source_peak;
    session_charge(admission, intent_stage_memory_bytes(admission->declaration));
    physicality_descriptor_source_observation_t observations[2];
    size_t observation_count = admission->observation_count;
    if (observation_count < 1 || observation_count > 2)
        elog(ERROR, "session_append_turns: exact projection observation count is invalid");
    for (size_t i = 0; i < observation_count; ++i) {
        observations[i].source_id = admission->source;
        observations[i].source_unit_id = admission->unit;
        observations[i].source_trust = SESSION_PROJECTION_PRIOR;
    }
    laplace_physicality_pg_admission_result *materialized = laplace_physicality_pg_materialize(
        (const intent_stage_t *const *)admission->raw, observation_count,
        NULL, 0, observations, observation_count, observed_unix,
        admission->maximum_bytes - admission->bytes, maximum_operations - SESSION_OPERATION_RESERVATION,
        (size_t) maximum_logical - composition_work);
    session_charge(admission, materialized->peak_bytes); admission->bytes -= materialized->peak_bytes;
    session_charge(admission, materialized->retained_bytes);
    const intent_stage_t *generated[4] = {admission->declaration,
        materialized->stages[0], materialized->stages[1], materialized->stages[2]};
    if (maximum_operations <= materialized->database_operations + SESSION_OPERATION_RESERVATION)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: generated stage sink has no operation grant")));
    if ((size_t) maximum_logical - composition_work <= materialized->logical_work)
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
            errmsg("session_append_turns: generated stage sink has no logical-work grant")));
    LaplaceGeneratedStageSinkLimits sink_limits = {
        (admission->maximum_bytes - admission->bytes) / sizeof(hash128_t),
        admission->maximum_bytes - admission->bytes,
        (size_t) maximum_logical - composition_work - materialized->logical_work,
        (uint32) (maximum_operations - SESSION_OPERATION_RESERVATION - materialized->database_operations)};
    LaplaceGeneratedStageSinkReceipt sink_receipt;
    laplace_generated_stage_sink(generated, 4, &sink_limits, &sink_receipt);
    /* Register display names against the actual ordinary source IDs. The
     * legacy canonical-name helper hashes whole label bytes and therefore is
     * not an identity constructor for these content-tree entities. */
    Oid registration_types[2] = {BYTEAARRAYOID, TEXTARRAYOID};
    Datum source_ids[2] = {hash128_to_datum(&admission->source),
        hash128_to_datum(&materialized->generated_source_id)};
    Datum names[2] = {CStringGetTextDatum(physicality_descriptor_session_source_name()),
        CStringGetTextDatum(physicality_descriptor_generated_source_name())};
    Datum registration_args[2] = {
        PointerGetDatum(construct_array(source_ids, 2, BYTEAOID, -1, false, TYPALIGN_INT)),
        PointerGetDatum(construct_array(names, 2, TEXTOID, -1, false, TYPALIGN_INT))};
    SPIPlanPtr registration = SPI_prepare(laplace_sql_query_text("conversation.register_projection_sources"),
        2, registration_types);
    if (registration == NULL || SPI_execute_plan(registration, registration_args, NULL, false, 1) != SPI_OK_SELECT ||
        SPI_processed != 1)
        elog(ERROR, "session_append_turns: registering actual projection source names failed");
    bool missing_mapping;
    Datum registered = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &missing_mapping);
    if (missing_mapping || DatumGetInt64(registered) != 2)
        elog(ERROR, "session_append_turns: projection source display name conflicts with its actual content ID");
    SPI_freetuptable(SPI_tuptable);
    SPI_freeplan(registration);
    size_t view_receipt_bytes = 0;
    char *view_receipt = session_view_receipt(admission, materialized, &view_receipt_bytes);
    laplace_physicality_pg_admission_release(materialized);
    Oid write_types[10] = {BYTEAOID, BYTEAOID, BYTEAOID, FLOAT8OID, FLOAT8OID,
                           FLOAT8OID, FLOAT8OID, BYTEAOID, INT4OID, TIMESTAMPTZOID};
    hash128_t hilbert_bytes;
    memcpy(&hilbert_bytes, hilbert.bytes, sizeof(hilbert_bytes));
    Datum values[10] = {hash128_to_datum(&physicality_id), PointerGetDatum(session),
        PointerGetDatum(wkb), Float8GetDatum(centroid[0]), Float8GetDatum(centroid[1]),
        Float8GetDatum(centroid[2]), Float8GetDatum(centroid[3]),
        hash128_to_datum(&hilbert_bytes), Int32GetDatum((int32) total), PG_GETARG_DATUM(2)};
    SPIPlanPtr write = session_plan(&session_write_plan,
        laplace_sql_query_text("conversation.write_manifest"), 10, write_types);
    if (SPI_execute_plan(write, values, NULL, false, 0) != SPI_OK_INSERT)
        elog(ERROR, "session_append_turns: persisting session trajectory failed");
    PopActiveSnapshot();
    laplace_spi_finish(spi_top);
    if (view_receipt != NULL)
    {
        ereport(NOTICE, (errmsg("session descriptor view unavailable; transaction pending: %s", view_receipt)));
        session_release(admission, view_receipt, view_receipt_bytes);
    }
    session_cleanup(admission);
    PG_RETURN_INT32((int32) total);
}
