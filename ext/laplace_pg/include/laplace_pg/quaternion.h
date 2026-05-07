/*
 * quaternion.h — QuaternionService public API.
 *
 * Phase 2 / Track B / Service B8.
 *
 * Quaternions over R^4 with (w + xi + yj + zk) algebra. The substrate stores
 * unit quaternions on S³ as POINT4D in the natural (x, y, z, w) layout (w
 * last) so the same memory shape works for general 4D points and for unit
 * quaternions. Quaternion algebra here uses the standard (i, j, k) basis;
 * the multiplication kernel computes:
 *   (a * b).w = a.w*b.w - a.x*b.x - a.y*b.y - a.z*b.z
 *   (a * b).x = a.w*b.x + a.x*b.w + a.y*b.z - a.z*b.y
 *   (a * b).y = a.w*b.y - a.x*b.z + a.y*b.w + a.z*b.x
 *   (a * b).z = a.w*b.z + a.x*b.y - a.y*b.x + a.z*b.w
 */

#ifndef LAPLACE_QUATERNION_H
#define LAPLACE_QUATERNION_H

#include "laplace_pg/geometry4d.h"

#ifdef __cplusplus
extern "C" {
#endif

void laplace_quaternion_multiply(const laplace_point4d_t *a,
                                 const laplace_point4d_t *b,
                                 laplace_point4d_t       *out);

void laplace_quaternion_conjugate(const laplace_point4d_t *q,
                                  laplace_point4d_t       *out);

void laplace_quaternion_inverse(const laplace_point4d_t *q,
                                laplace_point4d_t       *out);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_QUATERNION_H */
