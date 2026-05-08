/*
 * sql_hilbert.c — SQL binding for the inverse direction of HilbertCurveService
 * (Track B / B9). Forward direction (point4d → bigint) is bound via
 * point4d_ops_sql.c::point4d_hilbert_index. This file adds the inverse so
 * the curve is round-trippable at the SQL surface — useful for range-scan
 * decoding, B-tree-key-to-region reconstruction, and substrate diagnostics.
 *
 * Note: the inverse is lossy below 2^-15 because the forward direction
 * quantizes each axis to 16 bits. Caller MUST treat the returned point as
 * a representative grid-cell-corner, not the original POINT4D.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"

#include "laplace_pg/point4d_type.h"
#include "laplace_pg/hilbert.h"
#include "laplace_pg/geometry4d.h"

#include <stdint.h>

PG_FUNCTION_INFO_V1(hilbert_decode);
Datum hilbert_decode(PG_FUNCTION_ARGS)
{
    const int64 raw = PG_GETARG_INT64(0);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof(*out));
    laplace_hilbert_index_to_point4d((uint64_t) raw, (laplace_point4d_t *) out);
    PG_RETURN_POINT4D(out);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
