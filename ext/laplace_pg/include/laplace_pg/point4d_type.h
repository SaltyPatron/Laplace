/*
 * point4d_type.h — POINT4D PostgreSQL type definition (extension-side).
 *
 * The substrate's GEOMETRY4D type family begins here. POINT4D is the
 * foundational geometric type — 32 bytes, 4 IEEE 754 doubles in
 * (x, y, z, w) order. Independent custom PG type, NOT PostGIS GEOMETRYZM
 * with M repurposed.
 *
 * Storage model:
 *   - INTERNALLENGTH = 32 (fixed)
 *   - ALIGNMENT      = double
 *   - STORAGE        = plain (no toasting needed for 32 bytes)
 *   - PASSEDBYVALUE  = false (it's larger than a Datum)
 *
 * Phase 2 / Track B5 / Track C2 — type registration in the extension.
 */

#ifndef LAPLACE_POINT4D_TYPE_H
#define LAPLACE_POINT4D_TYPE_H

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"

typedef struct {
    double x;
    double y;
    double z;
    double w;
} laplace_point4d_pg_t;

#define DatumGetPoint4D(d)   ((laplace_point4d_pg_t *) DatumGetPointer(d))
#define Point4DGetDatum(p)   PointerGetDatum(p)
/* POINT4D is fixed-length 32 bytes, STORAGE=plain — never toasted, so
 * PG_DETOAST_DATUM is incorrect (it returns varlena* instead of Datum and
 * trips C4047 on MSVC). Pass the Datum straight through. */
#define PG_GETARG_POINT4D(n) DatumGetPoint4D(PG_GETARG_DATUM(n))
#define PG_RETURN_POINT4D(p) PG_RETURN_POINTER(p)

#endif /* LAPLACE_BUILD_PG_EXTENSION */

#endif /* LAPLACE_POINT4D_TYPE_H */
