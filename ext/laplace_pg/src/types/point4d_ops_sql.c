/*
 * point4d_ops_sql.c — SQL-bound operators on POINT4D.
 *
 * Each function dispatches to the corresponding native kernel
 * (Geometry4DService / S3DomainService) without copying the underlying
 * struct — the in-memory layout is identical (4 doubles).
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"

#include "laplace_pg/point4d_type.h"
#include "laplace_pg/geometry4d.h"
#include "laplace_pg/s3.h"
#include "laplace_pg/hilbert.h"
#include "laplace_pg/superfib.h"

PG_FUNCTION_INFO_V1(point4d_distance);
Datum point4d_distance(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *a = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *b = PG_GETARG_POINT4D(1);
    PG_RETURN_FLOAT8(laplace_point4d_distance(
        (const laplace_point4d_t *) a, (const laplace_point4d_t *) b));
}

PG_FUNCTION_INFO_V1(point4d_geodesic);
Datum point4d_geodesic(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *a = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *b = PG_GETARG_POINT4D(1);
    PG_RETURN_FLOAT8(laplace_s3_geodesic_distance(
        (const laplace_point4d_t *) a, (const laplace_point4d_t *) b));
}

PG_FUNCTION_INFO_V1(point4d_dot);
Datum point4d_dot(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *a = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *b = PG_GETARG_POINT4D(1);
    PG_RETURN_FLOAT8(laplace_point4d_dot(
        (const laplace_point4d_t *) a, (const laplace_point4d_t *) b));
}

PG_FUNCTION_INFO_V1(point4d_norm);
Datum point4d_norm(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *a = PG_GETARG_POINT4D(0);
    PG_RETURN_FLOAT8(laplace_point4d_norm((const laplace_point4d_t *) a));
}

PG_FUNCTION_INFO_V1(point4d_eq);
Datum point4d_eq(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *a = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *b = PG_GETARG_POINT4D(1);
    PG_RETURN_BOOL(a->x == b->x && a->y == b->y && a->z == b->z && a->w == b->w);
}

PG_FUNCTION_INFO_V1(point4d_slerp);
Datum point4d_slerp(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *a = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *b = PG_GETARG_POINT4D(1);
    double t = PG_GETARG_FLOAT8(2);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof *out);
    laplace_s3_slerp((const laplace_point4d_t *) a,
                     (const laplace_point4d_t *) b, t,
                     (laplace_point4d_t *) out);
    PG_RETURN_POINT4D(out);
}

PG_FUNCTION_INFO_V1(point4d_normalize);
Datum point4d_normalize(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *p = PG_GETARG_POINT4D(0);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof *out);
    laplace_s3_normalize((const laplace_point4d_t *) p, (laplace_point4d_t *) out);
    PG_RETURN_POINT4D(out);
}

PG_FUNCTION_INFO_V1(point4d_hilbert_index);
Datum point4d_hilbert_index(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *p = PG_GETARG_POINT4D(0);
    PG_RETURN_INT64((int64) laplace_hilbert_point4d_to_index(
        (const laplace_point4d_t *) p));
}

PG_FUNCTION_INFO_V1(point4d_super_fibonacci);
Datum point4d_super_fibonacci(PG_FUNCTION_ARGS)
{
    int32 i = PG_GETARG_INT32(0);
    int32 total = PG_GETARG_INT32(1);
    laplace_point4d_pg_t *out = (laplace_point4d_pg_t *) palloc(sizeof *out);
    double q[4];
    laplace_super_fibonacci_4d(i, total, q);
    out->x = q[0]; out->y = q[1]; out->z = q[2]; out->w = q[3];
    PG_RETURN_POINT4D(out);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
