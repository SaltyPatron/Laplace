/*
 * box4d_type.h — BOX4D PostgreSQL type definition.
 *
 * Axis-aligned 4D bounding box: (min_x, min_y, min_z, min_w,
 * max_x, max_y, max_z, max_w). Fixed-length 64 bytes. Used as the GiST
 * key for indexing POINT4D and LINESTRING4D — every indexed value reduces
 * to its BOX4D and the GiST opclass operates on those.
 *
 * Storage:
 *   - INTERNALLENGTH = 64 (fixed)
 *   - ALIGNMENT      = double
 *   - STORAGE        = plain
 *   - PASSEDBYVALUE  = false
 */

#ifndef LAPLACE_BOX4D_TYPE_H
#define LAPLACE_BOX4D_TYPE_H

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"

typedef struct {
    double min_x, min_y, min_z, min_w;
    double max_x, max_y, max_z, max_w;
} laplace_box4d_pg_t;

#define DatumGetBox4D(d)        ((laplace_box4d_pg_t *) DatumGetPointer(d))
#define Box4DGetDatum(p)        PointerGetDatum(p)
#define PG_GETARG_BOX4D(n)      DatumGetBox4D(PG_DETOAST_DATUM(PG_GETARG_DATUM(n)))
#define PG_RETURN_BOX4D(p)      PG_RETURN_POINTER(p)

#endif /* LAPLACE_BUILD_PG_EXTENSION */

#endif /* LAPLACE_BOX4D_TYPE_H */
