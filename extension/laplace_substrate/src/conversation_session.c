#include "postgres.h"
#include "miscadmin.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/array.h"
#include "utils/tuplestore.h"

#include "laplace/core/mantissa.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/trajectory.h"
#include "laplace/core/sql_catalog.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "trajectory_wkb.h"

PG_FUNCTION_INFO_V1(pg_laplace_session_turn_ids);
PG_FUNCTION_INFO_V1(pg_laplace_session_append_turns);

static SPIPlanPtr session_manifest_plan = NULL;
static SPIPlanPtr session_lock_plan = NULL;
static SPIPlanPtr session_coords_plan = NULL;
static SPIPlanPtr session_write_plan = NULL;

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
    Oid types[1] = {BYTEAOID};
    return session_plan(&session_manifest_plan,
        laplace_sql_query_text("conversation.manifest"), 1, types);
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
    hash128_t session_id, physicality_id;
    memcpy(&session_id, VARDATA_ANY(session), sizeof(session_id));
    laplace_physicality_id_compute(session_id, 1, &physicality_id);
    Datum args[1] = {hash128_to_datum(&physicality_id)};
    if (SPI_execute_plan(session_manifest(), args, NULL, true, 1) != SPI_OK_SELECT)
        elog(ERROR, "session_turn_ids: reading session manifest failed");

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
 * turn evidence and consensus. Row locking serializes appends to one session;
 * unrelated sessions do not share a process-local lock or transcript. */
Datum
pg_laplace_session_append_turns(PG_FUNCTION_ARGS)
{
    bytea *session = PG_GETARG_BYTEA_PP(0);
    ArrayType *turn_array = PG_GETARG_ARRAYTYPE_P(1);
    Datum *turns;
    bool *nulls;
    int added;
    bool spi_top = false;
    hash128_t session_id, physicality_id;
    size_t previous_count = 0;
    uint32 previous_vertices = 0;
    double *previous = NULL;

    if (VARSIZE_ANY_EXHDR(session) != sizeof(hash128_t))
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                       errmsg("session_append_turns: session id must contain 16 bytes")));
    if (ARR_NDIM(turn_array) > 1 || ARR_ELEMTYPE(turn_array) != BYTEAOID)
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                       errmsg("session_append_turns: turns must be a one-dimensional bytea array")));
    deconstruct_array(turn_array, BYTEAOID, -1, false, TYPALIGN_INT, &turns, &nulls, &added);
    if (added == 0) PG_RETURN_INT32(0);
    for (int i = 0; i < added; ++i)
        if (nulls[i] || VARSIZE_ANY_EXHDR(DatumGetByteaPP(turns[i])) != sizeof(hash128_t))
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                           errmsg("session_append_turns: each turn must contain 16 bytes")));
    memcpy(&session_id, VARDATA_ANY(session), sizeof(session_id));
    laplace_physicality_id_compute(session_id, 1, &physicality_id);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "session_append_turns: SPI_connect failed");
    Oid id_types[1] = {BYTEAOID};
    Datum args[1] = {PointerGetDatum(session)};
    SPIPlanPtr lock = session_plan(&session_lock_plan,
        laplace_sql_query_text("conversation.lock"), 1, id_types);
    if (SPI_execute_plan(lock, args, NULL, false, 1) != SPI_OK_SELECT || SPI_processed != 1)
        elog(ERROR, "session_append_turns: session entity must be admitted before its turns");
    SPI_freetuptable(SPI_tuptable);

    Datum manifest_args[1] = {hash128_to_datum(&physicality_id)};
    if (SPI_execute_plan(session_manifest(), manifest_args, NULL, false, 1) != SPI_OK_SELECT)
        elog(ERROR, "session_append_turns: reading session manifest failed");
    if (SPI_processed == 1)
    {
        bool isnull;
        Datum value = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
        if (!isnull)
        {
            const unsigned char *points = laplace_trajectory_wkb_points(
                DatumGetByteaPP(value), &previous_vertices);
            previous = palloc((Size) previous_vertices * 4 * sizeof(double));
            memcpy(previous, points, (Size) previous_vertices * 4 * sizeof(double));
            if (trajectory_constituent_count(previous, previous_vertices, &previous_count) != 0)
                elog(ERROR, "session_append_turns: invalid prior manifest");
        }
    }
    SPI_freetuptable(SPI_tuptable);

    if (previous_count > PG_INT32_MAX - (size_t) added ||
        previous_count + added > (MaxAllocSize - 9 - VARHDRSZ) / (4 * sizeof(double)))
        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                       errmsg("session_append_turns: session manifest exceeds allocation capacity")));
    size_t total = previous_count + added;
    SessionMembers members = {palloc(total * sizeof(hash128_t)), palloc0(total * sizeof(uint64))};
    if (previous && trajectory_visit_constituents(previous, previous_vertices,
                                                   collect_session_turn, &members) != 0)
        elog(ERROR, "session_append_turns: invalid prior turn sequence");
    if (previous) pfree(previous);
    for (int i = 0; i < added; ++i)
        memcpy(&members.ids[previous_count + i], VARDATA_ANY(DatumGetByteaPP(turns[i])), sizeof(hash128_t));

    Datum *ids = palloc(total * sizeof(Datum));
    for (size_t i = 0; i < total; ++i) ids[i] = hash128_to_datum(&members.ids[i]);
    ArrayType *all_turns = construct_array(ids, (int) total, BYTEAOID, -1, false, TYPALIGN_INT);
    Oid coord_types[1] = {BYTEAARRAYOID};
    Datum coord_args[1] = {PointerGetDatum(all_turns)};
    SPIPlanPtr coordinates = session_plan(&session_coords_plan,
        laplace_sql_query_text("conversation.coordinates"), 1, coord_types);
    if (SPI_execute_plan(coordinates, coord_args, NULL, false, 0) != SPI_OK_SELECT || SPI_processed != total)
        elog(ERROR, "session_append_turns: each turn requires an admitted content placement");
    double *coords = palloc(total * 4 * sizeof(double));
    uint8 max_tier = 0;
    for (size_t i = 0; i < total; ++i)
    {
        bool isnull;
        HeapTuple row = SPI_tuptable->vals[i];
        TupleDesc desc = SPI_tuptable->tupdesc;
        int16 tier = DatumGetInt16(SPI_getbinval(row, desc, 2, &isnull));
        if (isnull || tier < 0 || tier >= UINT8_MAX)
            elog(ERROR, "session_append_turns: invalid turn floor");
        max_tier = Max(max_tier, tier);
        if (i >= previous_count) members.flags[i] = laplace_vertex_flags((uint8) tier, false, 0);
        for (int axis = 0; axis < 4; ++axis)
        {
            Datum coordinate = SPI_getbinval(row, desc, axis + 3, &isnull);
            if (isnull) elog(ERROR, "session_append_turns: incomplete turn placement");
            coords[i * 4 + axis] = DatumGetFloat8(coordinate);
        }
    }
    SPI_freetuptable(SPI_tuptable);
    hash128_t version;
    hilbert128_t hilbert;
    double centroid[4];
    hash_composer_compose_node(max_tier + 1, members.ids, coords, total,
                               &version, centroid, &hilbert);
    double *packed = palloc(total * 4 * sizeof(double));
    size_t packed_count;
    if (trajectory_build_flagged_rle(members.ids, members.flags, total, packed, &packed_count) != 0)
        elog(ERROR, "session_append_turns: composing session trajectory failed");
    uint32 geometry_type = packed_count == 1 ? 3001 : 3002;
    Size header = packed_count == 1 ? 5 : 9;
    bytea *wkb = palloc(VARHDRSZ + header + packed_count * 4 * sizeof(double));
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
    laplace_spi_finish(spi_top);
    PG_RETURN_INT32((int32) total);
}
