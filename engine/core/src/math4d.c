#include "laplace/core/math4d.h"

#include <math.h>

double math4d_dot(const double a[4], const double b[4]) {
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
}

double math4d_norm(const double v[4]) {
    return sqrt(math4d_dot(v, v));
}

double math4d_radius_from_origin(const double v[4]) {
    return math4d_norm(v);
}

double math4d_distance_sq(const double a[4], const double b[4]) {
    const double dx = a[0] - b[0];
    const double dy = a[1] - b[1];
    const double dz = a[2] - b[2];
    const double dw = a[3] - b[3];
    return dx * dx + dy * dy + dz * dz + dw * dw;
}

double math4d_distance(const double a[4], const double b[4]) {
    return sqrt(math4d_distance_sq(a, b));
}

double math4d_angular_distance(const double a[4], const double b[4]) {
    /* Geodesic distance on S³: acos(a·b) when |a|=|b|=1. We normalize defensively
     * so callers can pass non-unit vectors without surprise; clamp the cosine into
     * [-1, 1] because FP roundoff can push it just outside the domain of acos. */
    const double na = math4d_norm(a);
    const double nb = math4d_norm(b);
    if (na == 0.0 || nb == 0.0) return 0.0;
    double c = math4d_dot(a, b) / (na * nb);
    if (c > 1.0) c = 1.0;
    if (c < -1.0) c = -1.0;
    return acos(c);
}

void math4d_add(const double a[4], const double b[4], double out[4]) {
    out[0] = a[0] + b[0];
    out[1] = a[1] + b[1];
    out[2] = a[2] + b[2];
    out[3] = a[3] + b[3];
}

void math4d_sub(const double a[4], const double b[4], double out[4]) {
    out[0] = a[0] - b[0];
    out[1] = a[1] - b[1];
    out[2] = a[2] - b[2];
    out[3] = a[3] - b[3];
}

void math4d_scale(const double a[4], double s, double out[4]) {
    out[0] = a[0] * s;
    out[1] = a[1] * s;
    out[2] = a[2] * s;
    out[3] = a[3] * s;
}

void math4d_centroid(const double* points, size_t n_points, double out[4]) {
    out[0] = 0.0;
    out[1] = 0.0;
    out[2] = 0.0;
    out[3] = 0.0;
    if (n_points == 0) return;
    /* Sequential sum in input order — deterministic across thread counts because
     * single-threaded by construction (TBB schedules above this kernel per ADR 0030). */
    for (size_t i = 0; i < n_points; ++i) {
        const double* p = points + i * 4;
        out[0] += p[0];
        out[1] += p[1];
        out[2] += p[2];
        out[3] += p[3];
    }
    const double inv = 1.0 / (double)n_points;
    out[0] *= inv;
    out[1] *= inv;
    out[2] *= inv;
    out[3] *= inv;
}
