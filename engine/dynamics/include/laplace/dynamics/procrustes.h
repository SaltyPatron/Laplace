#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Procrustes alignment — finds the rigid rotation + uniform scale +
 * translation that best aligns source-embedding points to target
 * physicality points in 4D.
 *
 * `procrustes_transform_t` is an opaque type-erased handle (per RULES.md
 * R14 + R22) — internally holds Eigen matrices that don't cross the
 * C ABI. The handle exists to bridge Eigen's C++ templates into the
 * C ABI; the upstream type (Eigen::Matrix) lives inside. */
typedef struct procrustes_transform procrustes_transform_t;

/* Fit a Procrustes transform aligning `source_pts` (n × source_dim,
 * row-major) to `target_pts` (n × 4 XYZM-packed doubles, row-major).
 * Returns NULL on failure; caller frees with procrustes_free. */
procrustes_transform_t*
procrustes_fit(const double* source_pts,
               size_t        n,
               size_t        source_dim,
               const double* target_pts);  /* n * 4 doubles */

/* Apply the transform to a single source vector → 4D point. */
void
procrustes_apply(const procrustes_transform_t* T,
                 const double*                 source_vec,  /* source_dim doubles */
                 size_t                        source_dim,
                 double                        out[4]);

/* Residual fit error (Frobenius norm of the alignment residual). */
double procrustes_residual(const procrustes_transform_t* T);

void   procrustes_free(procrustes_transform_t* T);

#ifdef __cplusplus
}
#endif
