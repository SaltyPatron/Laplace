/*
 * sql_quaternion.c — SQL bindings for QuaternionService (Track B / B8).
 *
 * The substrate stores unit quaternions on S³ as POINT4D in (x, y, z, w)
 * layout (w last). The C kernels in src/geometry4d/quaternion.c implement
 * standard Hamilton (i, j, k) algebra. These bindings expose them at the
 * SQL surface so quaternion-based composition, rotation, and S³ inverse
 * operations are queryable from substrate-side code without a P/Invoke
 * round-trip from managed callers.
 *
 * All functions IMMUTABLE PARALLEL SAFE STRICT — quaternion algebra has
 * no side effects and is null-propagating.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"

#include "laplace_pg/point4d_type.h"
#include "laplace_pg/quaternion.h"
#include "laplace_pg/geometry4d.h"

PG_FUNCTION_INFO_V1(quaternion_multiply);
Datum quaternion_multiply(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *a   = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *b   = PG_GETARG_POINT4D(1);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof(*out));
    laplace_quaternion_multiply((const laplace_point4d_t *) a,
                                (const laplace_point4d_t *) b,
                                (laplace_point4d_t *) out);
    PG_RETURN_POINT4D(out);
}

PG_FUNCTION_INFO_V1(quaternion_conjugate);
Datum quaternion_conjugate(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *q   = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof(*out));
    laplace_quaternion_conjugate((const laplace_point4d_t *) q,
                                 (laplace_point4d_t *) out);
    PG_RETURN_POINT4D(out);
}

PG_FUNCTION_INFO_V1(quaternion_inverse);
Datum quaternion_inverse(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *q   = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof(*out));
    laplace_quaternion_inverse((const laplace_point4d_t *) q,
                               (laplace_point4d_t *) out);
    PG_RETURN_POINT4D(out);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
