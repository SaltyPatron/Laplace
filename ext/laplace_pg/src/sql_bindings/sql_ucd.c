/*
 * sql_ucd.c — SQL bindings for UcdLookupService.
 *
 * Five scalar functions exposing the four fields of the codepoint entry
 * plus an existence predicate. All STRICT (NULL on NULL input) and
 * IMMUTABLE (the table is constant data).
 *
 * Substrate use cases:
 *   - F1 TextDecomposer SQL fast-path: laplace.ucd_hash($1) instead of
 *     joining the entity_tier0 table for codepoint atom resolution.
 *   - GiST/SP-GiST 4D query construction: laplace.ucd_position($1) gives
 *     the 4D query point without hitting physicality.
 *   - Hilbert-keyed range queries against entity_tier0: WHERE hilbert_key
 *     BETWEEN laplace.ucd_hilbert(@a) AND laplace.ucd_hilbert(@b).
 *   - prime_flags categorical pruning: WHERE laplace.has_prime(prime_flags,
 *     laplace.ucd_prime_flags($cp)).
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"

#include "laplace_pg/point4d_type.h"
#include "laplace_pg/ucd_lookup.h"

PG_FUNCTION_INFO_V1(pg_laplace_ucd_hash);
Datum pg_laplace_ucd_hash(PG_FUNCTION_ARGS)
{
    const int32 codepoint = PG_GETARG_INT32(0);
    const laplace_codepoint_entry_t *entry = laplace_ucd_lookup(codepoint);
    if (!entry) {
        PG_RETURN_NULL();
    }
    bytea *result = (bytea *) palloc(VARHDRSZ + 32);
    SET_VARSIZE(result, VARHDRSZ + 32);
    memcpy(VARDATA(result), entry->hash, 32);
    PG_RETURN_BYTEA_P(result);
}

PG_FUNCTION_INFO_V1(pg_laplace_ucd_position);
Datum pg_laplace_ucd_position(PG_FUNCTION_ARGS)
{
    const int32 codepoint = PG_GETARG_INT32(0);
    const laplace_codepoint_entry_t *entry = laplace_ucd_lookup(codepoint);
    if (!entry) {
        PG_RETURN_NULL();
    }
    laplace_point4d_pg_t *p = (laplace_point4d_pg_t *) palloc(sizeof(*p));
    p->x = entry->s3[0];
    p->y = entry->s3[1];
    p->z = entry->s3[2];
    p->w = entry->s3[3];
    PG_RETURN_POINT4D(p);
}

PG_FUNCTION_INFO_V1(pg_laplace_ucd_hilbert);
Datum pg_laplace_ucd_hilbert(PG_FUNCTION_ARGS)
{
    const int32 codepoint = PG_GETARG_INT32(0);
    const laplace_codepoint_entry_t *entry = laplace_ucd_lookup(codepoint);
    if (!entry) {
        PG_RETURN_NULL();
    }
    /* PG int8 is signed; the table's hilbert_index is unsigned 64. The
     * raw bit pattern round-trips through the signed conversion safely on
     * two's-complement platforms (which PG already requires). */
    PG_RETURN_INT64((int64) entry->hilbert_index);
}

PG_FUNCTION_INFO_V1(pg_laplace_ucd_prime_flags);
Datum pg_laplace_ucd_prime_flags(PG_FUNCTION_ARGS)
{
    const int32 codepoint = PG_GETARG_INT32(0);
    const laplace_codepoint_entry_t *entry = laplace_ucd_lookup(codepoint);
    if (!entry) {
        PG_RETURN_NULL();
    }
    PG_RETURN_INT64((int64) entry->prime_flags);
}

PG_FUNCTION_INFO_V1(pg_laplace_ucd_exists);
Datum pg_laplace_ucd_exists(PG_FUNCTION_ARGS)
{
    const int32 codepoint = PG_GETARG_INT32(0);
    PG_RETURN_BOOL(laplace_ucd_codepoint_exists(codepoint) != 0);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
