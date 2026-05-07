/*
 * linestring4d_type.h — LINESTRING4D PostgreSQL type definition.
 *
 * Variable-length type. Stores N >= 2 POINT4D vertices in sequence as the
 * trajectory through 4D space. Tier-1+ substrate compositions ARE
 * LINESTRING4D values: each vertex is a constituent's POINT4D position
 * (tier-0 codepoint S^3 placement, or tier-(N-1) centroid for higher tiers).
 *
 * Storage layout (variable-length):
 *   varlena header (4 or 1 byte short)
 *   uint32_t  vertex_count  (N)
 *   uint32_t  flags         (bit 0: unit-quaternion-on-S3 vertices flag)
 *   double    vertices[N * 4]   (N consecutive (x, y, z, w) tuples)
 *
 * The cache-line-aligned layout puts the 4-double vertex tuples directly
 * after the small fixed header so SIMD-loaded sequential scans (Frechet,
 * Hausdorff, length, centroid) hit contiguous memory.
 *
 * Per substrate invariants 2 + 4: vertex positions are content-derived
 * (super-Fibonacci for tier-0, parent-derived centroid for tier-N>=1).
 * The trajectory IS the composition; the centroid extracted from it is
 * the composition's representative position for KNN-on-meaning queries.
 */

#ifndef LAPLACE_LINESTRING4D_TYPE_H
#define LAPLACE_LINESTRING4D_TYPE_H

#ifdef LAPLACE_BUILD_PG_EXTENSION

#include "postgres.h"
#include "fmgr.h"

typedef struct {
    int32    vl_len_;       /* varlena header (use SET_VARSIZE / VARSIZE) */
    uint32   vertex_count;
    uint32   flags;
    double   vertices[FLEXIBLE_ARRAY_MEMBER];
} laplace_linestring4d_pg_t;

#define LAPLACE_LS4D_FLAG_S3_UNIT_QUATERNIONS  (1u << 0)

#define LAPLACE_LS4D_HEADER_BYTES        (offsetof(laplace_linestring4d_pg_t, vertices))
#define LAPLACE_LS4D_TOTAL_BYTES(n)      (LAPLACE_LS4D_HEADER_BYTES + (size_t)(n) * 4 * sizeof(double))

#define DatumGetLineString4D(d)          ((laplace_linestring4d_pg_t *) PG_DETOAST_DATUM(d))
#define LineString4DGetDatum(p)          PointerGetDatum(p)
#define PG_GETARG_LINESTRING4D(n)        DatumGetLineString4D(PG_GETARG_DATUM(n))
#define PG_RETURN_LINESTRING4D(p)        PG_RETURN_POINTER(p)

#endif /* LAPLACE_BUILD_PG_EXTENSION */

#endif /* LAPLACE_LINESTRING4D_TYPE_H */
