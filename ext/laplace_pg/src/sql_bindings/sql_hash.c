/*
 * sql_hash.c — SQL function bindings for BLAKE3HashService.
 *
 * Phase 2 / Track C / C3.
 *
 * Exposes the canonical content-addressing kernel as PostgreSQL functions
 * callable from SQL. Same C implementation that managed code calls via
 * P/Invoke — single source of truth, no parallel hash logic.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/bytea.h"
#include "catalog/pg_type.h"

#include "laplace_pg/hash.h"

/*
 * laplace_hash_atom(content bytea) RETURNS bytea
 * Returns the 32-byte BLAKE3-256 atom hash of the raw content bytes.
 */
PG_FUNCTION_INFO_V1(pg_laplace_hash_atom);
Datum pg_laplace_hash_atom(PG_FUNCTION_ARGS)
{
    bytea  *content = PG_GETARG_BYTEA_PP(0);
    const   uint8  *data = (const uint8 *) VARDATA_ANY(content);
    const   Size    len  = (Size) VARSIZE_ANY_EXHDR(content);

    bytea *out = (bytea *) palloc(VARHDRSZ + LAPLACE_HASH_BYTES);
    SET_VARSIZE(out, VARHDRSZ + LAPLACE_HASH_BYTES);
    laplace_hash_atom(data, len, (uint8 *) VARDATA(out));
    PG_RETURN_BYTEA_P(out);
}

/*
 * laplace_hash_composition(child_hashes bytea[], rle_counts integer[]) RETURNS bytea
 * Both arrays must be the same length. child_hashes elements must each be
 * exactly 32 bytes.
 */
PG_FUNCTION_INFO_V1(pg_laplace_hash_composition);
Datum pg_laplace_hash_composition(PG_FUNCTION_ARGS)
{
    ArrayType *hashes = PG_GETARG_ARRAYTYPE_P(0);
    ArrayType *counts = PG_GETARG_ARRAYTYPE_P(1);

    Datum   *hash_datums;
    bool    *hash_nulls;
    int      n_hashes;
    Datum   *count_datums;
    bool    *count_nulls;
    int      n_counts;

    deconstruct_array(hashes,  BYTEAOID, -1, false, 'i', &hash_datums,  &hash_nulls,  &n_hashes);
    deconstruct_array(counts,  INT4OID,   4,  true, 'i', &count_datums, &count_nulls, &n_counts);

    if (n_hashes != n_counts)
    {
        ereport(ERROR,
            (errcode(ERRCODE_ARRAY_ELEMENT_ERROR),
             errmsg("child_hashes and rle_counts must be the same length")));
    }

    uint8 *packed = (uint8 *) palloc((Size) n_hashes * LAPLACE_HASH_BYTES);
    int32 *rles   = (int32 *) palloc((Size) n_hashes * sizeof(int32));
    for (int i = 0; i < n_hashes; ++i)
    {
        if (hash_nulls[i])
        {
            ereport(ERROR, (errmsg("child_hashes[%d] is NULL", i + 1)));
        }
        bytea *h = DatumGetByteaPP(hash_datums[i]);
        if (VARSIZE_ANY_EXHDR(h) != LAPLACE_HASH_BYTES)
        {
            ereport(ERROR,
                (errmsg("child_hashes[%d] must be exactly %d bytes", i + 1, LAPLACE_HASH_BYTES)));
        }
        memcpy(packed + (Size) i * LAPLACE_HASH_BYTES, VARDATA_ANY(h), LAPLACE_HASH_BYTES);
        rles[i] = count_nulls[i] ? 1 : DatumGetInt32(count_datums[i]);
    }

    bytea *out = (bytea *) palloc(VARHDRSZ + LAPLACE_HASH_BYTES);
    SET_VARSIZE(out, VARHDRSZ + LAPLACE_HASH_BYTES);
    laplace_hash_composition(packed, rles, (size_t) n_hashes, (uint8 *) VARDATA(out));

    pfree(packed);
    pfree(rles);
    PG_RETURN_BYTEA_P(out);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
