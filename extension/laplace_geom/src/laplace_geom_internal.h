#ifndef LAPLACE_GEOM_INTERNAL_H
#define LAPLACE_GEOM_INTERNAL_H

#include "postgres.h"
#include "miscadmin.h"
#include "fmgr.h"
#include "funcapi.h"
#include "nodes/execnodes.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/hsearch.h"
#include "utils/tuplestore.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"

#include <math.h>

#include "laplace/core/version.h"
#include "laplace/core/hash128.h"
#include "laplace/core/math4d.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

#include "liblwgeom.h"

/* The xyzm buffer is one palloc of sizeof(double) * 4 per vertex, and Postgres
 * refuses any single palloc at or above MaxAllocSize. The vertex ceiling is
 * that quotient — derived from the allocator, not chosen. */
#define LAPLACE_XYZM_MAX_POINTS ((Size) (MaxAllocSize / (sizeof(double) * 4)))

LWGEOM *laplace_geom_lwgeom_from_datum(Datum d, GSERIALIZED **out_gser);
void laplace_geom_require_point4d(LWGEOM *geom, const char *fn_name, POINT4D *out);
void laplace_geom_require_xyzm_capacity(Size npoints, const char *fn_name);
double *laplace_geom_to_xyzm_buffer(LWGEOM *geom, const char *fn_name, size_t *out_npoints);
Datum laplace_geom_gserialized_point4d_datum(double x, double y, double z, double m);

#endif
