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
