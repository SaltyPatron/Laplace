/*
 * point4d_binary.c — POINT4D binary send/receive functions.
 *
 * Wire format: 4 IEEE 754 doubles in network byte order (PG convention).
 * pq_sendfloat8 / pq_getmsgfloat8 handle the byte order.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "libpq/pqformat.h"

#include "laplace_pg/point4d_type.h"

PG_FUNCTION_INFO_V1(point4d_send);
Datum point4d_send(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *pt = PG_GETARG_POINT4D(0);
    StringInfoData buf;
    pq_begintypsend(&buf);
    pq_sendfloat8(&buf, pt->x);
    pq_sendfloat8(&buf, pt->y);
    pq_sendfloat8(&buf, pt->z);
    pq_sendfloat8(&buf, pt->w);
    PG_RETURN_BYTEA_P(pq_endtypsend(&buf));
}

PG_FUNCTION_INFO_V1(point4d_recv);
Datum point4d_recv(PG_FUNCTION_ARGS)
{
    StringInfo buf = (StringInfo) PG_GETARG_POINTER(0);
    laplace_point4d_pg_t *pt = (laplace_point4d_pg_t *) palloc(sizeof *pt);
    pt->x = pq_getmsgfloat8(buf);
    pt->y = pq_getmsgfloat8(buf);
    pt->z = pq_getmsgfloat8(buf);
    pt->w = pq_getmsgfloat8(buf);
    PG_RETURN_POINT4D(pt);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
