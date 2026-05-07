/*
 * sql_superfib.c — SQL function bindings for SuperFibonacciService.
 *
 * Phase 2 / Track C / C3.
 *
 * laplace_super_fibonacci_4d(i integer, total integer) RETURNS bytea
 *   Returns a 32-byte bytea containing 4 little-endian doubles (x, y, z, w)
 *   placed on S³ at sample i of total via the Marc Alexa CVPR 2022 spiral.
 *
 * The bytea layout matches the substrate's physicality.position storage
 * format (octet_length = 32, 4 doubles in (x, y, z, w) order).
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "utils/bytea.h"

#include "laplace_pg/superfib.h"

PG_FUNCTION_INFO_V1(pg_laplace_super_fibonacci_4d);
Datum pg_laplace_super_fibonacci_4d(PG_FUNCTION_ARGS)
{
    int32 i     = PG_GETARG_INT32(0);
    int32 total = PG_GETARG_INT32(1);

    bytea *out = (bytea *) palloc(VARHDRSZ + 32);
    SET_VARSIZE(out, VARHDRSZ + 32);
    double q[4];
    laplace_super_fibonacci_4d(i, total, q);

    /* Emit as little-endian doubles. memcpy is endian-agnostic on the
     * host; PG runs on LE hardware in practice (x86_64 + ARM64 LE). */
    memcpy(VARDATA(out), q, sizeof q);
    PG_RETURN_BYTEA_P(out);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
