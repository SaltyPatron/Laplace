/*
 * point4d_ops.c — POINT4D vector primitives.
 *
 * Scalar implementation. SIMD (AVX2 / AVX-512) variants land alongside
 * KnnExactService (B15) where they pay back; for single-point ops the
 * scalar form is faster than the dispatch overhead.
 */

#include "laplace_pg/geometry4d.h"

#include <math.h>

double laplace_point4d_dot(const laplace_point4d_t *a, const laplace_point4d_t *b)
{
    return a->x * b->x + a->y * b->y + a->z * b->z + a->w * b->w;
}

double laplace_point4d_norm(const laplace_point4d_t *a)
{
    return sqrt(laplace_point4d_dot(a, a));
}

double laplace_point4d_distance(const laplace_point4d_t *a, const laplace_point4d_t *b)
{
    const double dx = a->x - b->x;
    const double dy = a->y - b->y;
    const double dz = a->z - b->z;
    const double dw = a->w - b->w;
    return sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
}

void laplace_point4d_add(const laplace_point4d_t *a, const laplace_point4d_t *b,
                         laplace_point4d_t *out)
{
    out->x = a->x + b->x;
    out->y = a->y + b->y;
    out->z = a->z + b->z;
    out->w = a->w + b->w;
}

void laplace_point4d_sub(const laplace_point4d_t *a, const laplace_point4d_t *b,
                         laplace_point4d_t *out)
{
    out->x = a->x - b->x;
    out->y = a->y - b->y;
    out->z = a->z - b->z;
    out->w = a->w - b->w;
}

void laplace_point4d_scale(const laplace_point4d_t *a, double k,
                           laplace_point4d_t *out)
{
    out->x = a->x * k;
    out->y = a->y * k;
    out->z = a->z * k;
    out->w = a->w * k;
}

void laplace_point4d_vertex_centroid(const laplace_point4d_t *points,
                                     size_t                   n_points,
                                     laplace_point4d_t       *out)
{
    out->x = 0.0; out->y = 0.0; out->z = 0.0; out->w = 0.0;
    if (n_points == 0) {
        return;
    }
    for (size_t i = 0; i < n_points; ++i) {
        out->x += points[i].x;
        out->y += points[i].y;
        out->z += points[i].z;
        out->w += points[i].w;
    }
    const double inv = 1.0 / (double) n_points;
    out->x *= inv;
    out->y *= inv;
    out->z *= inv;
    out->w *= inv;
}
