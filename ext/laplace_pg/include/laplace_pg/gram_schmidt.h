/*
 * gram_schmidt.h — GramSchmidtService public API.
 *
 * Phase 2 / Track B / Service B16.
 *
 * Modified Gram-Schmidt orthonormalization. Used by:
 *   - LaplacianEigenmapService (B17) when projecting an N-dim embedding
 *     space down to 4D for S³ placement (firefly extraction)
 *   - any pipeline that needs an orthonormal basis built from sample
 *     vectors
 *
 * Modified (vs classical) variant: stable enough for the small embedding
 * dimensions we project from (typically 384/768/1024/3584 input dim → 4
 * output basis vectors). Householder is overkill here.
 */

#ifndef LAPLACE_GRAM_SCHMIDT_H
#define LAPLACE_GRAM_SCHMIDT_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Orthonormalize n_vectors row-wise vectors of dim dim, in place. Each row
 * is `dim` consecutive doubles; rows live at vectors + i*dim.
 *
 * Returns the number of linearly independent rows produced (rows whose post-
 * subtraction norm exceeded the rank tolerance; trailing rows that collapsed
 * are zeroed).
 */
size_t laplace_gram_schmidt_orthonormalize(double *vectors,
                                           size_t  n_vectors,
                                           size_t  dim,
                                           double  rank_tol);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_GRAM_SCHMIDT_H */
