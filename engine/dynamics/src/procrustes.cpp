#include "laplace/dynamics/procrustes.h"

#include <cstddef>

/* Real implementation lands Chunk 6 Story 6.8 — Procrustes alignment via
 * oneMKL SVD path through Eigen (EIGEN_USE_MKL_ALL). For now, opaque-handle
 * stub satisfies linkage.
 *
 * The internal struct will hold Eigen::Matrix<double, 4, N_source_dim> for
 * the rotation, a double for scale, and Eigen::Vector4d for translation.
 * Those types live INSIDE the struct; the C ABI sees only the opaque
 * pointer. */

struct procrustes_transform {
    int _placeholder;
};

extern "C"
procrustes_transform_t* procrustes_fit(const double* source_pts,
                                       size_t        n,
                                       size_t        source_dim,
                                       const double* target_pts) {
    (void)source_pts; (void)n; (void)source_dim; (void)target_pts;
    return nullptr;
}

extern "C"
void procrustes_apply(const procrustes_transform_t* T,
                      const double*                 source_vec,
                      size_t                        source_dim,
                      double                        out[4]) {
    (void)T; (void)source_vec; (void)source_dim;
    out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 0;
}

extern "C" double procrustes_residual(const procrustes_transform_t* T) {
    (void)T;
    return 0.0;
}

extern "C" void procrustes_free(procrustes_transform_t* T) {
    delete T;
}
