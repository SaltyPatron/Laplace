/*
 * point4d_io.c — POINT4D text input/output functions.
 *
 * Wire format: "POINT4D(x y z w)" — case-insensitive prefix, parenthesized,
 * space-separated G17 doubles. Plain "(x y z w)" or "x y z w" are also
 * accepted. Output uses the canonical "POINT4D(x y z w)" form with G17
 * (round-trip-safe) double formatting.
 */

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"

#include "laplace_pg/point4d_type.h"

#include <stdio.h>

PG_FUNCTION_INFO_V1(point4d_in);
Datum point4d_in(PG_FUNCTION_ARGS)
{
    char *str = PG_GETARG_CSTRING(0);
    laplace_point4d_pg_t *pt = (laplace_point4d_pg_t *) palloc(sizeof *pt);

    int n = sscanf(str, " POINT4D ( %lf %lf %lf %lf )",
                   &pt->x, &pt->y, &pt->z, &pt->w);
    if (n != 4) {
        n = sscanf(str, " point4d ( %lf %lf %lf %lf )",
                   &pt->x, &pt->y, &pt->z, &pt->w);
    }
    if (n != 4) {
        n = sscanf(str, " ( %lf %lf %lf %lf )",
                   &pt->x, &pt->y, &pt->z, &pt->w);
    }
    if (n != 4) {
        n = sscanf(str, " %lf %lf %lf %lf ",
                   &pt->x, &pt->y, &pt->z, &pt->w);
    }
    if (n != 4) {
        ereport(ERROR,
            (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
             errmsg("invalid input syntax for type point4d: \"%s\"", str)));
    }
    PG_RETURN_POINT4D(pt);
}

PG_FUNCTION_INFO_V1(point4d_out);
Datum point4d_out(PG_FUNCTION_ARGS)
{
    laplace_point4d_pg_t *pt = PG_GETARG_POINT4D(0);
    /* G17 ensures round-trip from binary → text → binary is exact. */
    char *result = psprintf("POINT4D(%.17g %.17g %.17g %.17g)",
                            pt->x, pt->y, pt->z, pt->w);
    PG_RETURN_CSTRING(result);
}

#endif /* LAPLACE_BUILD_PG_EXTENSION */
