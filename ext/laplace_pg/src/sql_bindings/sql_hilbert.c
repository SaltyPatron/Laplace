/*
 * sql_hilbert.c — SQL function bindings for HilbertCurveService.
 *
 * Phase 2 / Track C / C3.
 *
 * laplace_hilbert_index(position bytea) RETURNS bigint
 *   Compute the 64-bit Hilbert index for a physicality position bytea
 *   (4 little-endian doubles in [-1, +1]). Used to populate
 *   physicality.hilbert_index on insert and to drive locality-ordered
 *   B-tree probes alongside the 4D GiST/SP-GiST indexes.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "utils/bytea.h"

#include "laplace_pg/hilbert.h"
#include "laplace_pg/geometry4d.h"

PG_FUNCTION_INFO_V1(pg_laplace_hilbert_index);
Datum pg_laplace_hilbert_index(PG_FUNCTION_ARGS)
{
    bytea *pos = PG_GETARG_BYTEA_PP(0);
    if (VARSIZE_ANY_EXHDR(pos) != 32)
    {
        ereport(ERROR,
            (errmsg("position bytea must be exactly 32 bytes (4 doubles)")));
    }
    laplace_point4d_t p;
    memcpy(&p, VARDATA_ANY(pos), 32);
    PG_RETURN_INT64((int64) laplace_hilbert_point4d_to_index(&p));
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
