/*
 * quaternion.c — quaternion algebra over (x, y, z, w).
 */

#include "laplace_pg/quaternion.h"

void laplace_quaternion_multiply(const laplace_point4d_t *a,
                                 const laplace_point4d_t *b,
                                 laplace_point4d_t       *out)
{
    const double ax = a->x, ay = a->y, az = a->z, aw = a->w;
    const double bx = b->x, by = b->y, bz = b->z, bw = b->w;
    out->w = aw * bw - ax * bx - ay * by - az * bz;
    out->x = aw * bx + ax * bw + ay * bz - az * by;
    out->y = aw * by - ax * bz + ay * bw + az * bx;
    out->z = aw * bz + ax * by - ay * bx + az * bw;
}

void laplace_quaternion_conjugate(const laplace_point4d_t *q,
                                  laplace_point4d_t       *out)
{
    out->x = -q->x;
    out->y = -q->y;
    out->z = -q->z;
    out->w =  q->w;
}

void laplace_quaternion_inverse(const laplace_point4d_t *q,
                                laplace_point4d_t       *out)
{
    const double n2 = q->x * q->x + q->y * q->y + q->z * q->z + q->w * q->w;
    if (n2 == 0.0) {
        out->x = 0.0; out->y = 0.0; out->z = 0.0; out->w = 0.0;
        return;
    }
    const double inv = 1.0 / n2;
    out->x = -q->x * inv;
    out->y = -q->y * inv;
    out->z = -q->z * inv;
    out->w =  q->w * inv;
}
