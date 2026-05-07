/*
 * s3_ops.c — S³ domain operations: normalize, geodesic, slerp, eigenvalue
 *            centroid (Markley quaternion averaging via power iteration on
 *            the 4x4 accumulator matrix).
 */

#include "laplace_pg/s3.h"

#include <math.h>
#include <string.h>

int laplace_s3_is_on_sphere(const laplace_point4d_t *p, double tol)
{
    const double n = laplace_point4d_norm(p);
    return fabs(n - 1.0) <= tol;
}

void laplace_s3_normalize(const laplace_point4d_t *p, laplace_point4d_t *out)
{
    const double n = laplace_point4d_norm(p);
    if (n == 0.0 || !isfinite(n)) {
        out->x = 0.0; out->y = 0.0; out->z = 0.0; out->w = 1.0;
        return;
    }
    const double inv = 1.0 / n;
    out->x = p->x * inv;
    out->y = p->y * inv;
    out->z = p->z * inv;
    out->w = p->w * inv;
}

double laplace_s3_geodesic_distance(const laplace_point4d_t *a,
                                    const laplace_point4d_t *b)
{
    double d = laplace_point4d_dot(a, b);
    if (d >  1.0) { d =  1.0; }
    if (d < -1.0) { d = -1.0; }
    return acos(d);
}

void laplace_s3_slerp(const laplace_point4d_t *a,
                      const laplace_point4d_t *b,
                      double                   t,
                      laplace_point4d_t       *out)
{
    double dot = laplace_point4d_dot(a, b);

    /* Take the short way around if the inputs face opposite hemispheres. */
    laplace_point4d_t b_eff = *b;
    if (dot < 0.0) {
        b_eff.x = -b_eff.x; b_eff.y = -b_eff.y; b_eff.z = -b_eff.z; b_eff.w = -b_eff.w;
        dot = -dot;
    }

    /* For very close inputs fall back to LERP+normalize (avoids div-by-zero in sin). */
    if (dot > 0.9995) {
        out->x = a->x + t * (b_eff.x - a->x);
        out->y = a->y + t * (b_eff.y - a->y);
        out->z = a->z + t * (b_eff.z - a->z);
        out->w = a->w + t * (b_eff.w - a->w);
        laplace_s3_normalize(out, out);
        return;
    }

    if (dot >  1.0) { dot =  1.0; }
    if (dot < -1.0) { dot = -1.0; }
    const double theta_0 = acos(dot);
    const double theta   = theta_0 * t;
    const double sin_th0 = sin(theta_0);
    const double s_a     = sin(theta_0 - theta) / sin_th0;
    const double s_b     = sin(theta)           / sin_th0;
    out->x = s_a * a->x + s_b * b_eff.x;
    out->y = s_a * a->y + s_b * b_eff.y;
    out->z = s_a * a->z + s_b * b_eff.z;
    out->w = s_a * a->w + s_b * b_eff.w;
}

/*
 * Markley quaternion averaging — solve max eigenvalue of
 *   M = sum_i w_i * p_i * p_i^T   (4x4 symmetric PSD)
 * via 32-step power iteration with Rayleigh quotient deflation. Sufficient
 * accuracy for substrate use; replaced with closed-form 4x4 eigensolver
 * (LAPACKE_dsyev) once MKL is wired during Track B finalization.
 */
void laplace_s3_eigenvalue_centroid(const laplace_point4d_t *points,
                                    const double            *weights,
                                    size_t                   n_points,
                                    laplace_point4d_t       *out)
{
    if (n_points == 0) {
        out->x = 0.0; out->y = 0.0; out->z = 0.0; out->w = 1.0;
        return;
    }

    /* Accumulate M (row-major 4x4). */
    double M[16];
    memset(M, 0, sizeof M);
    for (size_t i = 0; i < n_points; ++i) {
        const double w = weights ? weights[i] : 1.0;
        const double p[4] = {points[i].x, points[i].y, points[i].z, points[i].w};
        for (int r = 0; r < 4; ++r) {
            for (int c = 0; c < 4; ++c) {
                M[r * 4 + c] += w * p[r] * p[c];
            }
        }
    }

    /* Power iteration starting from the vertex centroid (a reasonable seed). */
    laplace_point4d_t seed;
    laplace_point4d_vertex_centroid(points, n_points, &seed);
    if (laplace_point4d_norm(&seed) < 1e-12) {
        seed.x = 1.0; seed.y = 0.0; seed.z = 0.0; seed.w = 0.0;
    }
    double v[4] = {seed.x, seed.y, seed.z, seed.w};

    for (int iter = 0; iter < 64; ++iter) {
        double y[4] = {0.0, 0.0, 0.0, 0.0};
        for (int r = 0; r < 4; ++r) {
            for (int c = 0; c < 4; ++c) {
                y[r] += M[r * 4 + c] * v[c];
            }
        }
        const double n = sqrt(y[0]*y[0] + y[1]*y[1] + y[2]*y[2] + y[3]*y[3]);
        if (n == 0.0 || !isfinite(n)) {
            break;
        }
        const double inv = 1.0 / n;
        const double ndiff =
              fabs(v[0] - y[0]*inv) + fabs(v[1] - y[1]*inv)
            + fabs(v[2] - y[2]*inv) + fabs(v[3] - y[3]*inv);
        v[0] = y[0] * inv;
        v[1] = y[1] * inv;
        v[2] = y[2] * inv;
        v[3] = y[3] * inv;
        if (ndiff < 1e-14) {
            break;
        }
    }

    out->x = v[0];
    out->y = v[1];
    out->z = v[2];
    out->w = v[3];
}
