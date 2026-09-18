#include "postgres.h"

#include <string.h>

#include "funcapi.h"
#include "utils/builtins.h"

#include "spi_common.h"
#include "laplace/core/mantissa.h"

PG_FUNCTION_INFO_V1(pg_laplace_model_testimony_decode);

Datum
pg_laplace_model_testimony_decode(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea *bits = PG_GETARG_BYTEA_PP(0);
    Size bytes = VARSIZE_ANY_EXHDR(bits);
    const Size vertex_bytes = sizeof(double) * 4;

    InitMaterializedSRF(fcinfo, 0);
    if (bytes == 0)
        PG_RETURN_NULL();
    if (bytes % vertex_bytes != 0)
        ereport(ERROR,
                (errmsg("model testimony trajectory has %zu bytes, not complete XYZM vertices",
                        (size_t) bytes)));

    const char *raw = VARDATA_ANY(bits);
    int count = (int) (bytes / vertex_bytes);
    for (int logical = 0; logical < count; ++logical)
    {
        double vertex[4];
        hash128_t token;
        int64 score = 0;
        uint16 games = 0;
        uint16 packed_ordinal = 0;
        Datum values[5];
        bool nulls[5] = {false, false, false, false, false};

        memcpy(vertex, raw + (Size) logical * vertex_bytes, vertex_bytes);
        if (laplace_testimony_unpack_vertex(
                vertex, &token, &score, &games, &packed_ordinal) != 0)
            ereport(ERROR,
                    (errmsg("model testimony trajectory vertex %d is not testimony",
                            logical)));

        values[0] = hash128_to_datum(&token);
        values[1] = Int64GetDatum(score);
        values[2] = Int32GetDatum(logical);
        values[3] = Int32GetDatum((int32) packed_ordinal);
        values[4] = Int32GetDatum((int32) games);
        tuplestore_putvalues(
            rsinfo->setResult, rsinfo->setDesc,
            values, nulls);
        pfree(DatumGetPointer(values[0]));
    }
    PG_RETURN_NULL();
}
