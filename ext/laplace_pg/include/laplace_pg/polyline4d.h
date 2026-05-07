/*
 * polyline4d.h — Geometry4DOperatorsService polyline distances.
 *
 * Phase 2 / Track B / Service B6 (interim — Frechet + Hausdorff for
 * point-sets and polylines; full operator surface lands incrementally).
 *
 * Discrete Fréchet distance — Eiter & Mannila, "Computing Discrete Fréchet
 * Distance" (1994). O(n*m) dynamic-programming. Used by IRanking +
 * IFrayedEdgeDetector for substrate-path comparison in 4D.
 *
 * Symmetric Hausdorff distance — for unordered firefly clouds (consensus
 * tightness measurement). O(n*m) brute-force; promoted to KD-tree-backed
 * version once Voronoi4DService (B12) wires CGAL.
 */

#ifndef LAPLACE_POLYLINE4D_H
#define LAPLACE_POLYLINE4D_H

#include <stddef.h>

#include "laplace_pg/geometry4d.h"

#ifdef __cplusplus
extern "C" {
#endif

double laplace_frechet_distance_4d(const laplace_point4d_t *p, size_t np,
                                   const laplace_point4d_t *q, size_t nq);

double laplace_hausdorff_distance_4d(const laplace_point4d_t *p, size_t np,
                                     const laplace_point4d_t *q, size_t nq);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_POLYLINE4D_H */
