/*
 * extension/laplace_geom/src/laplace_geom.c
 *
 * PG_FUNCTION_INFO_V1 wrappers for laplace_geom. Per RULES.md R6, this
 * file is thin marshalling code only -- no math. Engine kernels in
 * liblaplace_core do the work.
 *
 * Functions exposed (Story 1.13 + Chunk 2.6+):
 *   - pg_laplace_geom_version       : extension self-identity
 *   - pg_laplace_hash128_blake3     : BLAKE3-128 of an arbitrary bytea
 *   - pg_laplace_hash128_merkle     : tier-prefixed Merkle composition
 *                                     over an array of bytea(16) children
 *
 * Geometry-typed wrappers (hilbert4d_encode/decode, mantissa_pack/unpack,
 * ST_*_4d family) need liblwgeom marshalling and land in a follow-up.
 */

#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "catalog/pg_type.h"

#include "laplace/core/version.h"
#include "laplace/core/hash128.h"

PG_MODULE_MAGIC;

/* ---------------------------------------------------------------- */
/* pg_laplace_geom_version                                          */
/* ---------------------------------------------------------------- */

PG_FUNCTION_INFO_V1(pg_laplace_geom_version);

Datum
pg_laplace_geom_version(PG_FUNCTION_ARGS)
{
    const char* v = laplace_core_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}

/* ---------------------------------------------------------------- */
/* pg_laplace_hash128_blake3(bytea) -> bytea(16)                    */
/* ---------------------------------------------------------------- */

PG_FUNCTION_INFO_V1(pg_laplace_hash128_blake3);

Datum
pg_laplace_hash128_blake3(PG_FUNCTION_ARGS)
{
    bytea*       data_arg = PG_GETARG_BYTEA_PP(0);
    const uint8* data     = (const uint8*) VARDATA_ANY(data_arg);
    Size         data_len = VARSIZE_ANY_EXHDR(data_arg);

    /* Allocate a bytea(16) for the output. */
    bytea* result = (bytea*) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(result, VARHDRSZ + sizeof(hash128_t));

    /* Engine kernel does the work. Writes 16 bytes into result's data area. */
    hash128_blake3(data, (size_t) data_len, (hash128_t*) VARDATA(result));

    PG_RETURN_BYTEA_P(result);
}

/* ---------------------------------------------------------------- */
/* pg_laplace_hash128_merkle(int2 tier, bytea[] children) -> bytea(16) */
/* ---------------------------------------------------------------- */

PG_FUNCTION_INFO_V1(pg_laplace_hash128_merkle);

Datum
pg_laplace_hash128_merkle(PG_FUNCTION_ARGS)
{
    int16        tier_arg     = PG_GETARG_INT16(0);
    ArrayType*   children_arr = PG_GETARG_ARRAYTYPE_P(1);

    /* Validate tier fits in uint8. */
    if (tier_arg < 0 || tier_arg > 255)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("hash128_merkle: tier %d out of range [0, 255]", tier_arg)));

    /* Validate children array shape: 1-d, bytea elements. */
    if (ARR_NDIM(children_arr) != 1)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("hash128_merkle: children must be a one-dimensional array")));
    if (ARR_ELEMTYPE(children_arr) != BYTEAOID)
        ereport(ERROR,
                (errcode(ERRCODE_DATATYPE_MISMATCH),
                 errmsg("hash128_merkle: children must be bytea[]")));

    Datum*     elems;
    bool*      nulls;
    int        n_elems;

    deconstruct_array(children_arr, BYTEAOID, -1, false, 'i',
                      &elems, &nulls, &n_elems);

    /* Build a contiguous hash128_t[] of the children, validating each is 16 bytes. */
    hash128_t* children = (hash128_t*) palloc(sizeof(hash128_t) * (Size) n_elems);
    for (int i = 0; i < n_elems; i++) {
        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("hash128_merkle: child hash at index %d is NULL", i)));
        bytea* child = DatumGetByteaPP(elems[i]);
        Size child_len = VARSIZE_ANY_EXHDR(child);
        if (child_len != sizeof(hash128_t))
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("hash128_merkle: child hash at index %d has length %zu, expected %zu",
                            i, child_len, sizeof(hash128_t))));
        memcpy(&children[i], VARDATA_ANY(child), sizeof(hash128_t));
    }

    /* Allocate the 16-byte result bytea. */
    bytea* result = (bytea*) palloc(VARHDRSZ + sizeof(hash128_t));
    SET_VARSIZE(result, VARHDRSZ + sizeof(hash128_t));

    /* Engine kernel does the work. */
    hash128_merkle((uint8_t) tier_arg, children, (size_t) n_elems,
                   (hash128_t*) VARDATA(result));

    PG_RETURN_BYTEA_P(result);
}
