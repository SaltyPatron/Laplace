#include "laplace/dynamics/gram_schmidt.h"

#include <cstddef>

/* Real implementation lands Chunk 6 Story 6.7 — Eigen HouseholderQR for
 * numerical stability. Stub satisfies linkage. */

extern "C"
int gram_schmidt_orthonormalize(double* vectors,
                                size_t  n_vecs,
                                size_t  dim) {
    (void)vectors; (void)n_vecs; (void)dim;
    return -1;
}
