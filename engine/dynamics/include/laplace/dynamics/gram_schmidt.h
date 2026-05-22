#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Gram-Schmidt orthonormalization — used during AI-model ingestion to
 * orthonormalize a basis of source-embedding directions before
 * projecting into the substrate (Chunk 6 Story 6.7).
 *
 * Internally uses Eigen's HouseholderQR for numerical stability
 * (modified Gram-Schmidt is numerically unstable for ill-conditioned
 * input bases). No custom types — operates on raw double arrays. */

/* In-place orthonormalization of `vectors` (n_vecs × dim, row-major).
 * Returns 0 on success; nonzero if the input basis is rank-deficient. */
int gram_schmidt_orthonormalize(double* vectors,
                                size_t  n_vecs,
                                size_t  dim);

#ifdef __cplusplus
}
#endif
