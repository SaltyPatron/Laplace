/*
 * geometry4d.h — Geometry4DService primitives.
 *
 * Phase 2 / Track B / Service B5 (interim — POINT4D primitives only).
 *
 * Full GEOMETRY4D PostgreSQL type family (POINT4D / LINESTRING4D /
 * POLYGON4D / MULTI*4D / GEOMETRYCOLLECTION4D / TRIANGLE4D / TIN4D /
 * POLYHEDRALSURFACE4D / CIRCULARSTRING4D / COMPOUNDCURVE4D /
 * CURVEPOLYGON4D / MULTICURVE4D / MULTISURFACE4D / BOX4D, plus WKT/WKB IO,
 * full ST_4D_* operator surface) lands incrementally — see Track B5/B6 in
 * the build plan. This header provides the foundational POINT4D struct and
 * vector primitives that S3DomainService, QuaternionService, and the
 * Voronoi / KNN / Laplacian-eigenmap services build on.
 *
 * Pre-rejected substitution: GEOMETRYZM with M repurposed. Laplace owns its
 * own type family with its own subtype OIDs; existing PostGIS stays for
 * naturally low-dim modalities.
 */

#ifndef LAPLACE_GEOMETRY4D_H
#define LAPLACE_GEOMETRY4D_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 4D point as (x, y, z, w). Same memory layout used everywhere — managed
 * Point4D records, native quaternion routines, S3 normalize, SuperFib output,
 * and (eventually) the GEOMETRY4D wire format all agree on this ordering.
 */
typedef struct {
    double x;
    double y;
    double z;
    double w;
} laplace_point4d_t;

/* Vector primitives (no allocation; results written through the out pointer). */
double laplace_point4d_dot(const laplace_point4d_t *a, const laplace_point4d_t *b);
double laplace_point4d_norm(const laplace_point4d_t *a);
double laplace_point4d_distance(const laplace_point4d_t *a, const laplace_point4d_t *b);

void   laplace_point4d_add(const laplace_point4d_t *a, const laplace_point4d_t *b,
                           laplace_point4d_t *out);
void   laplace_point4d_sub(const laplace_point4d_t *a, const laplace_point4d_t *b,
                           laplace_point4d_t *out);
void   laplace_point4d_scale(const laplace_point4d_t *a, double k,
                             laplace_point4d_t *out);

/* Average of n points (vertex centroid in the 4-ball). */
void   laplace_point4d_vertex_centroid(const laplace_point4d_t *points,
                                       size_t                   n_points,
                                       laplace_point4d_t       *out);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_GEOMETRY4D_H */
