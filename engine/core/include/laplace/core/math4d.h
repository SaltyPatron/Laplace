#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 4D math kernels — operate on raw XYZM-packed double buffers.
 *
 * Per RULES.md R1: no parallel datatypes. PostGIS's POINT4D
 * (liblwgeom) is the canonical XYZM coordinate struct
 * ({double x, y, z, m}); these kernels match its memory layout so
 * PG-extension wrappers can cast POINT4D* directly. C# side packs
 * NTS Coordinate (XYZM) into double[] before P/Invoke. The engine
 * library has zero geometry-type dependencies — pure math on FP64
 * buffers, SIMD-friendly, no struct-accessor overhead.
 *
 * Implementations land in Chunk 1 (Story 1.1 onward). */

/* Single-point operations (each point is 4 doubles in XYZM order). */
double math4d_dot(const double a[4], const double b[4]);
double math4d_norm(const double v[4]);
double math4d_radius_from_origin(const double v[4]);
double math4d_distance(const double a[4], const double b[4]);
double math4d_distance_sq(const double a[4], const double b[4]);
double math4d_angular_distance(const double a[4], const double b[4]);

void   math4d_add(const double a[4], const double b[4], double out[4]);
void   math4d_sub(const double a[4], const double b[4], double out[4]);
void   math4d_scale(const double a[4], double s, double out[4]);

/* Multi-point operations — `points` is a flat buffer of n_points*4 doubles. */
void   math4d_centroid(const double* points, size_t n_points, double out[4]);

/* Discrete Fréchet distance (Eiter-Mannila 1994) between two 4D trajectories.
 * Trajectory P is `np` points (flat n*4 doubles), Q is `nq` points (n*4 doubles).
 * Returns the Fréchet distance, or NaN if either trajectory is empty.
 * O(np*nq) time + memory (heap-allocated DP table; freed before return). */
double math4d_frechet(const double* p, size_t np, const double* q, size_t nq);

/* Symmetric Hausdorff distance between two 4D point sets.
 * `a` is `na` points (flat n*4 doubles), `b` is `nb` points (n*4 doubles).
 * Returns max(directed_Hausdorff(a→b), directed_Hausdorff(b→a)), or NaN if
 * either set is empty. O(na*nb) time, O(1) extra space. */
double math4d_hausdorff(const double* a, size_t na, const double* b, size_t nb);

#ifdef __cplusplus
}
#endif
