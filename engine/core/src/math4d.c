#include "laplace/core/math4d.h"

/* Real implementations land Chunk 1 Story 1.1. These stubs satisfy
 * linkage so the C ABI is testable end-to-end; bodies are placeholder.
 * The header contract is the lock-in surface. */

double math4d_dot(const double a[4], const double b[4]) {
    (void)a; (void)b;
    return 0.0;
}

double math4d_norm(const double v[4]) {
    (void)v;
    return 0.0;
}

double math4d_radius_from_origin(const double v[4]) {
    (void)v;
    return 0.0;
}

double math4d_distance(const double a[4], const double b[4]) {
    (void)a; (void)b;
    return 0.0;
}

double math4d_distance_sq(const double a[4], const double b[4]) {
    (void)a; (void)b;
    return 0.0;
}

double math4d_angular_distance(const double a[4], const double b[4]) {
    (void)a; (void)b;
    return 0.0;
}

void math4d_add(const double a[4], const double b[4], double out[4]) {
    (void)a; (void)b;
    out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 0;
}

void math4d_sub(const double a[4], const double b[4], double out[4]) {
    (void)a; (void)b;
    out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 0;
}

void math4d_scale(const double a[4], double s, double out[4]) {
    (void)a; (void)s;
    out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 0;
}

void math4d_centroid(const double* points, size_t n_points, double out[4]) {
    (void)points; (void)n_points;
    out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 0;
}
