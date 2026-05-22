#include "laplace/dynamics/eigenmaps.h"

#include <cstddef>

/* Real implementation lands Chunk 6 Story 6.6 — Laplacian eigenmaps via
 * Spectra sparse symmetric eigensolver on Eigen sparse matrices.
 * Stub satisfies linkage. */

extern "C"
int laplacian_eigenmaps(const double* high_dim_pts,
                        size_t        n,
                        size_t        high_dim,
                        size_t        k_neighbors,
                        size_t        target_dim,
                        double*       low_dim_out) {
    (void)high_dim_pts; (void)n; (void)high_dim;
    (void)k_neighbors; (void)target_dim; (void)low_dim_out;
    return -1;
}
