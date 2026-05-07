/*
 * s3.h — S3DomainService public API.
 *
 * Phase 2 / Track B / Service B7.
 *
 * S³ is the unit 3-sphere in R^4: { p in R^4 : ||p|| = 1 }. The substrate
 * places every Unicode codepoint atom on S³ via super-Fibonacci. Composition
 * centroids, AI model fireflies, and other physicalities project here too
 * (each in their own physicality partition).
 *
 * Operations:
 *   normalize         — project a 4D point radially onto S³
 *   geodesic_distance — angular arc length on S³ (acos of clamped dot product)
 *   slerp             — spherical linear interpolation between two unit points
 *   eigenvalue_centroid — Markley's quaternion averaging (largest eigenvector
 *                         of the accumulated outer product matrix), the
 *                         correct way to average unit quaternions / S³ points
 *
 * Pre-rejected substitution: arithmetic averaging of unit quaternions and
 * dividing by norm. That biases toward the local tangent plane and is wrong
 * for clouds spanning more than ~30° on S³.
 */

#ifndef LAPLACE_S3_H
#define LAPLACE_S3_H

#include "laplace_pg/geometry4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* True if ||p|| - 1 is within tol. */
int  laplace_s3_is_on_sphere(const laplace_point4d_t *p, double tol);

/* Project radially onto S³. If ||p|| == 0 returns (0,0,0,1). */
void laplace_s3_normalize(const laplace_point4d_t *p, laplace_point4d_t *out);

/* Angular distance in radians in [0, pi]. Clamps the dot to [-1, 1] for
 * numerical safety. NOTE: this does NOT identify antipodal points; callers
 * that want quaternion-equivalence (q ≡ -q) should pass abs(dot).
 */
double laplace_s3_geodesic_distance(const laplace_point4d_t *a,
                                    const laplace_point4d_t *b);

/* Spherical linear interpolation. t in [0, 1]. */
void laplace_s3_slerp(const laplace_point4d_t *a,
                      const laplace_point4d_t *b,
                      double                   t,
                      laplace_point4d_t       *out);

/* Markley's quaternion averaging — largest eigenvector of the 4x4 matrix
 * M = sum_i w_i * p_i * p_i^T. Implementation uses the power iteration
 * since the matrix is small (4x4) and Eigen/Spectra would be heavy here.
 *
 * weights may be NULL (treated as all-1.0).
 */
void laplace_s3_eigenvalue_centroid(const laplace_point4d_t *points,
                                    const double            *weights,
                                    size_t                   n_points,
                                    laplace_point4d_t       *out);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_S3_H */
