#include "postgres.h"
#include "miscadmin.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "spi_stream.h"
#include "trajectory_wkb.h"
#include "laplace/core/sql_catalog.h"

#include "laplace/core/mantissa.h"

/*
 * chess.syzygy_transition(position)
 *
 * Reads one recorded transition out of trajectory structure. The trajectory
 * membership index selects the manifests, attested by the ChessSyzygy source,
 * that contain the position; C then scans each manifest in ordinal order for a
 * (position, move, successor) run with roles 0, 1, 2 (flags bits 8-9) and
 * returns the move, the successor, the 3-bit result class (flags bits 10-12) and
 * the zigzag-coded DTZ (flags bits 16+) of the position vertex. The first match
 * ends the read.
 */

static SPIPlanPtr syzygy_chunk_plan = NULL;

static void
ensure_syzygy_plan(void)
{
    if (syzygy_chunk_plan != NULL)
        return;

    {
        Oid argtypes[1] = { BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(
            laplace_sql_query_text("chess.syzygy_carriers"), 1, argtypes, CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "chess_syzygy_transition: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "chess_syzygy_transition: SPI_keepplan failed");
        syzygy_chunk_plan = plan;
    }
}

static bytea *
id_bytea(const hash128_t *id)
{
    bytea *out = (bytea *) palloc(VARHDRSZ + sizeof(*id));

    SET_VARSIZE(out, VARHDRSZ + sizeof(*id));
    memcpy(VARDATA(out), id, sizeof(*id));
    return out;
}

typedef struct {
    ReturnSetInfo *result;
    hash128_t position;
} SyzygyRead;

static bool
receive_syzygy_transition(TupleTableSlot *row, void *opaque)
{
    SyzygyRead *read = opaque;
    bool isnull;
    Datum value = slot_getattr(row, 1, &isnull);
    if (isnull) return true;
    uint32 count;
    const unsigned char *points = laplace_trajectory_wkb_points(DatumGetByteaPP(value), &count);
    for (uint32 v = 0; v + 2 < count; v++) {
        CHECK_FOR_INTERRUPTS();
        double vertex[4]; mantissa_payload_t from, move, next;
        memcpy(vertex, points + (Size)v * 32, sizeof(vertex));
        mantissa_unpack(vertex, &from);
        if (((from.flags >> 8) & 3u) != 0 ||
            memcmp(&from.entity_id, &read->position, sizeof(read->position)) != 0) continue;
        memcpy(vertex, points + (Size)(v + 1) * 32, sizeof(vertex));
        mantissa_unpack(vertex, &move);
        memcpy(vertex, points + (Size)(v + 2) * 32, sizeof(vertex));
        mantissa_unpack(vertex, &next);
        if (((move.flags >> 8) & 3u) != 1 || ((next.flags >> 8) & 3u) != 2) continue;
        uint32 zigzag = (uint32)(from.flags >> 16);
        int32 dtz = (int32)(zigzag >> 1) ^ -((int32)zigzag & 1);
        Datum values[] = {PointerGetDatum(id_bytea(&move.entity_id)),
            PointerGetDatum(id_bytea(&next.entity_id)),
            Int32GetDatum((int32)((from.flags >> 10) & 7u)), Int32GetDatum(dtz)};
        bool nulls[] = {false, false, false, false};
        tuplestore_putvalues(read->result->setResult, read->result->setDesc, values, nulls);
        return false;
    }
    return true;
}

PG_FUNCTION_INFO_V1(pg_laplace_chess_syzygy_transition);

Datum
pg_laplace_chess_syzygy_transition(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          position_datum;
    bytea         *position;
    Datum          one[1];
    ArrayType     *positions;
    Datum          args[1];
    char           nulls[1] = { ' ' };
    bool           spi_top = false;

    InitMaterializedSRF(fcinfo, 0);
    if (PG_ARGISNULL(0))
        return (Datum) 0;

    position_datum = PG_GETARG_DATUM(0);
    position = DatumGetByteaPP(position_datum);
    if (VARSIZE_ANY_EXHDR(position) != 16)
        ereport(ERROR,
                (errcode(ERRCODE_STRING_DATA_LENGTH_MISMATCH),
                 errmsg("chess.syzygy_transition: position id must be 16 bytes")));

    /* Copy the detoasted input into the array's owning memory. */
    one[0] = copy_bytea_datum(position_datum);
    positions = construct_array(one, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    args[0] = PointerGetDatum(positions);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "chess_syzygy_transition: SPI_connect failed");
    ensure_syzygy_plan();

    SyzygyRead read = {.result = rsinfo};
    memcpy(&read.position, VARDATA_ANY(position), sizeof(read.position));
    Oid types[] = {BYTEAARRAYOID};
    laplace_spi_stream_until(syzygy_chunk_plan, 1, types, args, nulls,
        receive_syzygy_transition, &read);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
