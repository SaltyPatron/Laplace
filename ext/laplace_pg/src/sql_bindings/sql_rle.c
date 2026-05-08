/*
 * sql_rle.c — SQL bindings for RleService (Track B / B3).
 *
 * Substrate invariant 3 (CLAUDE.md): "Entities referenced as FEW times as
 * physically possible. RLE everywhere there's adjacency. Maximum dedup."
 * RLE is the substrate's adjacency-collapse primitive at every tier — the
 * SQL surface needs encode/decode so DDL paths (entity_child rle_count
 * column emission, sequence run-length compression) and ad-hoc tooling
 * can collapse runs without round-tripping through managed code.
 *
 * Encode returns a 2-column composite (values bytea, counts integer[])
 * via OUT parameters + heap_form_tuple. Decode takes the pair and
 * reconstructs the original byte sequence.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "funcapi.h"
#include "catalog/pg_type.h"

#include "laplace_pg/rle.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

PG_FUNCTION_INFO_V1(rle_encode_bytes);
Datum rle_encode_bytes(PG_FUNCTION_ARGS)
{
    bytea  *input  = PG_GETARG_BYTEA_PP(0);
    size_t  in_len = (size_t) VARSIZE_ANY_EXHDR(input);

    uint8_t *out_values = (uint8_t *) palloc(in_len > 0 ? in_len : 1);
    int32_t *out_counts = (int32_t *) palloc((in_len > 0 ? in_len : 1) * sizeof(int32_t));
    size_t   n_runs     = laplace_rle_encode_bytes(
        (const uint8_t *) VARDATA_ANY(input),
        in_len,
        out_values,
        out_counts);

    /* values bytea */
    bytea *values_bytea = (bytea *) palloc(VARHDRSZ + n_runs);
    SET_VARSIZE(values_bytea, VARHDRSZ + n_runs);
    if (n_runs > 0) {
        memcpy(VARDATA(values_bytea), out_values, n_runs);
    }

    /* counts int4[] */
    Datum *count_datums = (Datum *) palloc(sizeof(Datum) * (n_runs > 0 ? n_runs : 1));
    for (size_t i = 0; i < n_runs; ++i) {
        count_datums[i] = Int32GetDatum(out_counts[i]);
    }
    int dims[1]  = { (int) n_runs };
    int lbs[1]   = { 1 };
    ArrayType *counts_array = construct_md_array(
        count_datums, NULL, 1, dims, lbs,
        INT4OID, sizeof(int32), true, TYPALIGN_INT);

    /* Composite tuple */
    TupleDesc tupdesc;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR,
                (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                 errmsg("rle_encode_bytes called in non-composite context")));
    }
    BlessTupleDesc(tupdesc);

    Datum     out_values_d[2] = { PointerGetDatum(values_bytea), PointerGetDatum(counts_array) };
    bool      out_nulls[2]    = { false, false };
    HeapTuple tuple           = heap_form_tuple(tupdesc, out_values_d, out_nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}

PG_FUNCTION_INFO_V1(rle_decode_bytes);
Datum rle_decode_bytes(PG_FUNCTION_ARGS)
{
    bytea     *values_bytea = PG_GETARG_BYTEA_PP(0);
    ArrayType *counts_array = PG_GETARG_ARRAYTYPE_P(1);

    if (ARR_NDIM(counts_array) > 1) {
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("rle_decode_bytes: counts must be a 1-D integer array")));
    }
    if (ARR_HASNULL(counts_array)) {
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("rle_decode_bytes: counts must not contain NULL")));
    }

    const size_t   n_values = (size_t) VARSIZE_ANY_EXHDR(values_bytea);
    const int32_t  n_counts = ArrayGetNItems(ARR_NDIM(counts_array), ARR_DIMS(counts_array));
    if ((size_t) n_counts != n_values) {
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("rle_decode_bytes: length(values)=%zu does not match length(counts)=%d",
                        n_values, n_counts)));
    }

    /* Total decoded byte count = sum(counts). */
    const int32_t *counts_data = (const int32_t *) ARR_DATA_PTR(counts_array);
    size_t total = 0;
    for (int32_t i = 0; i < n_counts; ++i) {
        if (counts_data[i] <= 0) {
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("rle_decode_bytes: counts[%d]=%d must be positive", i, counts_data[i])));
        }
        total += (size_t) counts_data[i];
    }

    bytea *result = (bytea *) palloc(VARHDRSZ + total);
    SET_VARSIZE(result, VARHDRSZ + total);
    const size_t produced = laplace_rle_decode_bytes(
        (const uint8_t *) VARDATA_ANY(values_bytea),
        counts_data,
        n_values,
        (uint8_t *) VARDATA(result),
        total);
    if (produced != total) {
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("rle_decode_bytes: kernel produced %zu bytes, expected %zu",
                        produced, total)));
    }
    PG_RETURN_BYTEA_P(result);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
