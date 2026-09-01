#include "postgres.h"
#include "miscadmin.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "spi_common.h"
#include "spi_nested.h"

#include "laplace/core/mantissa.h"

/*
 * chess.syzygy_transition(position)
 *
 * SQL owns only the indexed candidate reduction: the projection-constituent
 * GIN finds Syzygy chunks containing the requested position.  C owns the
 * ordered trajectory decode and adjacency operation.  The previous SQL body
 * expanded every point through ST_DumpPoints/laplace_mantissa_unpack, built a
 * materialized vertex relation, and self-joined it twice; a single live lookup
 * exceeded 20 seconds on a 6,144-vertex chunk.  One WKB row plus a linear native
 * scan is the same boundary used by geometry_successors and continuations.
 */
static const char *SYZYGY_CHUNK_QUERY =
    "SELECT public.ST_AsBinary(p.trajectory) "
    "FROM laplace.physicalities p "
    "JOIN laplace.entities e ON e.id = p.entity_id "
    "WHERE e.first_observed_by = laplace.source_id('ChessSyzygy') "
    "  AND p.type = 3::smallint "
    "  AND p.trajectory IS NOT NULL "
    "  AND public.laplace_trajectory_constituent_ids(p.trajectory) @> $1 "
    "ORDER BY p.entity_id";

static SPIPlanPtr syzygy_chunk_plan = NULL;

static void
ensure_syzygy_plan(void)
{
    if (syzygy_chunk_plan != NULL)
        return;

    {
        Oid argtypes[1] = { BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(
            SYZYGY_CHUNK_QUERY, 1, argtypes, CURSOR_OPT_PARALLEL_OK);

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
    Portal         portal;
    bool           found = false;

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

    portal = SPI_cursor_open(NULL, syzygy_chunk_plan, args, nulls, true);
    if (portal == NULL)
        elog(ERROR, "chess_syzygy_transition: cursor open failed: %s",
             SPI_result_code_string(SPI_result));

    while (!found)
    {
        SPI_cursor_fetch(portal, true, 32);
        if (SPI_processed == 0)
            break;

        for (uint64 row = 0; row < SPI_processed && !found; row++)
        {
            HeapTuple tuple = SPI_tuptable->vals[row];
            TupleDesc desc = SPI_tuptable->tupdesc;
            bool      isnull;
            Datum     wkb_datum = SPI_getbinval(tuple, desc, 1, &isnull);
            bytea    *wkb;
            const unsigned char *body;
            const unsigned char *start;
            Size      len;
            uint32    wkb_type;
            uint32    npoints;
            Size      header;

            if (isnull)
                continue;
            wkb = DatumGetByteaPP(wkb_datum);
            start = (const unsigned char *) VARDATA_ANY(wkb);
            body = start;
            len = VARSIZE_ANY_EXHDR(wkb);
            if (len < 9 || body[0] != 1)
                ereport(ERROR,
                        (errmsg("chess.syzygy_transition: unexpected WKB framing")));
            memcpy(&wkb_type, body + 1, 4);
            if (wkb_type != 3002u)
                continue;
            memcpy(&npoints, body + 5, 4);
            header = 9;
            if ((Size) npoints > (len - header) / 32)
                ereport(ERROR,
                        (errmsg("chess.syzygy_transition: truncated trajectory WKB")));
            body += header;

            for (uint32 v = 0; v + 2 < npoints; v++)
            {
                double             xyzm[4];
                mantissa_payload_t from;
                mantissa_payload_t move;
                mantissa_payload_t next;
                int                role_from;
                int                role_move;
                int                role_next;
                uint32             zigzag;
                int32              dtz;
                Datum              values[4];
                bool               out_nulls[4] = { false, false, false, false };

                memcpy(xyzm, body + (Size) v * 32, 32);
                mantissa_unpack(xyzm, &from);
                role_from = (int) ((from.flags >> 8) & 3u);
                if (role_from != 0 ||
                    memcmp(&from.entity_id, VARDATA_ANY(position), 16) != 0)
                    continue;

                memcpy(xyzm, body + (Size) (v + 1) * 32, 32);
                mantissa_unpack(xyzm, &move);
                memcpy(xyzm, body + (Size) (v + 2) * 32, 32);
                mantissa_unpack(xyzm, &next);
                role_move = (int) ((move.flags >> 8) & 3u);
                role_next = (int) ((next.flags >> 8) & 3u);
                if (role_move != 1 || role_next != 2)
                    continue;

                zigzag = (uint32) (from.flags >> 16);
                dtz = (int32) (zigzag >> 1) ^ -((int32) zigzag & 1);
                values[0] = PointerGetDatum(id_bytea(&move.entity_id));
                values[1] = PointerGetDatum(id_bytea(&next.entity_id));
                values[2] = Int32GetDatum((int32) ((from.flags >> 10) & 7u));
                values[3] = Int32GetDatum(dtz);
                tuplestore_putvalues(
                    rsinfo->setResult, rsinfo->setDesc, values, out_nulls);
                found = true;
                break;
            }
            CHECK_FOR_INTERRUPTS();
        }
        SPI_freetuptable(SPI_tuptable);
    }

    SPI_cursor_close(portal);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
