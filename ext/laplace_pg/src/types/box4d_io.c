/*
 * box4d_io.c — BOX4D text + binary I/O.
 *
 * Wire format (text): "BOX4D(min_x min_y min_z min_w, max_x max_y max_z max_w)"
 * Wire format (binary): 8 doubles in network byte order.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "libpq/pqformat.h"

#include "laplace_pg/box4d_type.h"

#include <stdio.h>

PG_FUNCTION_INFO_V1(box4d_in);
Datum box4d_in(PG_FUNCTION_ARGS)
{
    char *str = PG_GETARG_CSTRING(0);
    laplace_box4d_pg_t *box = (laplace_box4d_pg_t *) palloc(sizeof *box);
    int n = sscanf(str, " BOX4D ( %lf %lf %lf %lf , %lf %lf %lf %lf )",
                   &box->min_x, &box->min_y, &box->min_z, &box->min_w,
                   &box->max_x, &box->max_y, &box->max_z, &box->max_w);
    if (n != 8) {
        n = sscanf(str, " ( %lf %lf %lf %lf , %lf %lf %lf %lf )",
                   &box->min_x, &box->min_y, &box->min_z, &box->min_w,
                   &box->max_x, &box->max_y, &box->max_z, &box->max_w);
    }
    if (n != 8) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                        errmsg("invalid input syntax for type box4d: \"%s\"", str)));
    }
    PG_RETURN_BOX4D(box);
}

PG_FUNCTION_INFO_V1(box4d_out);
Datum box4d_out(PG_FUNCTION_ARGS)
{
    laplace_box4d_pg_t *box = PG_GETARG_BOX4D(0);
    char *result = psprintf("BOX4D(%.17g %.17g %.17g %.17g, %.17g %.17g %.17g %.17g)",
                            box->min_x, box->min_y, box->min_z, box->min_w,
                            box->max_x, box->max_y, box->max_z, box->max_w);
    PG_RETURN_CSTRING(result);
}

PG_FUNCTION_INFO_V1(box4d_send);
Datum box4d_send(PG_FUNCTION_ARGS)
{
    laplace_box4d_pg_t *box = PG_GETARG_BOX4D(0);
    StringInfoData buf;
    pq_begintypsend(&buf);
    pq_sendfloat8(&buf, box->min_x); pq_sendfloat8(&buf, box->min_y);
    pq_sendfloat8(&buf, box->min_z); pq_sendfloat8(&buf, box->min_w);
    pq_sendfloat8(&buf, box->max_x); pq_sendfloat8(&buf, box->max_y);
    pq_sendfloat8(&buf, box->max_z); pq_sendfloat8(&buf, box->max_w);
    PG_RETURN_BYTEA_P(pq_endtypsend(&buf));
}

PG_FUNCTION_INFO_V1(box4d_recv);
Datum box4d_recv(PG_FUNCTION_ARGS)
{
    StringInfo buf = (StringInfo) PG_GETARG_POINTER(0);
    laplace_box4d_pg_t *box = (laplace_box4d_pg_t *) palloc(sizeof *box);
    box->min_x = pq_getmsgfloat8(buf); box->min_y = pq_getmsgfloat8(buf);
    box->min_z = pq_getmsgfloat8(buf); box->min_w = pq_getmsgfloat8(buf);
    box->max_x = pq_getmsgfloat8(buf); box->max_y = pq_getmsgfloat8(buf);
    box->max_z = pq_getmsgfloat8(buf); box->max_w = pq_getmsgfloat8(buf);
    PG_RETURN_BOX4D(box);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
