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

/* Geodesic (Riemannian) log/exp maps on the unit 3-sphere S³ ⊂ R⁴.
 *
 * These are the chart maps for the Karcher mean iteration: Log lifts a point
 * on the sphere into the tangent space at a base point (a 4-vector orthogonal
 * to the base, whose norm equals the geodesic/angular distance); Exp walks the
 * geodesic from the base in a tangent direction back onto the sphere.
 *
 * Both assume `base` is (numerically) a unit vector. `p` for Log is normalized
 * defensively. Standard sphere formulas (do Carmo, Riemannian Geometry §3):
 *   Log_base(p) = θ · v̂,  where θ = acos(base·p), v̂ = (p − (base·p)·base)/|·|
 *   Exp_base(t) = cos|t|·base + sin|t|·(t/|t|)
 * Degeneracies: p ≈ base → zero tangent; p ≈ −base (antipodal, θ ≈ π) →
 * the geodesic direction is undefined, so Log returns a zero tangent (the
 * documented fallback — an antipode contributes no pull, mirroring the
 * undefinedness of the geodesic at the cut locus). */
void   math4d_log_s3(const double base[4], const double p[4], double out_tangent[4]);
void   math4d_exp_s3(const double base[4], const double tangent[4], double out[4]);

/* Weighted Karcher (Fréchet) mean of N points on S³ — the geodesic mean, NOT
 * the Euclidean centroid. `points` is a flat buffer of n_points*4 doubles
 * (each a unit 4-vector; non-unit input is normalized defensively). `weights`
 * is an optional n_points buffer of non-negative weights; pass NULL for the
 * uniform (all-1) case.
 *
 * Algorithm (Karcher 1977; Pennec 2006): initialize at the normalized
 * Euclidean mean, then iterate — Log every point into the tangent space at the
 * current estimate, take the weighted average tangent, Exp it back onto the
 * sphere — until the tangent-update norm < `tol` or `max_iters` is reached.
 * Deterministic: fixed input-order reductions, single-threaded (matches
 * math4d_centroid's CBWR-aligned init), no top-k.
 *
 * Edge cases: n_points == 0 → out zeroed; single point → that point
 * (normalized); all-equal points → that point; an initial Euclidean mean that
 * is (near-)zero (antipodally balanced cluster) → falls back to the first
 * point as the seed (documented degeneracy — the mean is genuinely
 * non-unique). Writes the unit-norm mean into out[4]. */
void   math4d_karcher_mean(const double* points, size_t n_points,
                           const double* weights, double tol, int max_iters,
                           double out[4]);

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
