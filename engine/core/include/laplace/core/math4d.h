#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

double math4d_dot(const double a[4], const double b[4]);
double math4d_norm(const double v[4]);
double math4d_radius_from_origin(const double v[4]);
double math4d_distance(const double a[4], const double b[4]);
double math4d_distance_sq(const double a[4], const double b[4]);
double math4d_angular_distance(const double a[4], const double b[4]);

/*
 * Batched form of math4d_angular_distance: a_flat is n*4 doubles (candidate i's
 * x,y,z,w at a_flat[i*4..i*4+3]), b is the single fixed anchor point shared
 * across all candidates, out[i] receives angular_distance(a_flat+i*4, b).
 * Dispatches to an AVX2 kernel when available (runtime CPUID check, resolved
 * once and cached), with a scalar fallback identical to calling
 * math4d_angular_distance in a loop -- always correct, AVX2 only changes
 * performance, never behavior (beyond ordinary floating-point reassociation).
 */
void math4d_angular_distance_batch(const double *a_flat, int n, const double b[4],
                                   double *out);

void   math4d_add(const double a[4], const double b[4], double out[4]);
void   math4d_sub(const double a[4], const double b[4], double out[4]);
void   math4d_scale(const double a[4], double s, double out[4]);

void   math4d_centroid(const double* points, size_t n_points, double out[4]);

void   math4d_log_s3(const double base[4], const double p[4], double out_tangent[4]);
void   math4d_exp_s3(const double base[4], const double tangent[4], double out[4]);

void   math4d_karcher_mean(const double* points, size_t n_points,
                           const double* weights, double tol, int max_iters,
                           double out[4]);

double math4d_frechet(const double* p, size_t np, const double* q, size_t nq);

double math4d_hausdorff(const double* a, size_t na, const double* b, size_t nb);

#ifdef __cplusplus
}
#endif
