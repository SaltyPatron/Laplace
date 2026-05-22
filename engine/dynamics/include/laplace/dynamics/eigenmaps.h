#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Laplacian eigenmaps — non-linear dimensionality reduction via the
 * spectrum of the graph Laplacian. Used during AI-model ingestion to
 * project high-dim source embeddings into the 4D substrate (DESIGN.md
 * IV; Chunk 6).
 *
 * Internally uses Spectra (header-only, on Eigen) for sparse symmetric
 * eigensolver. No custom types — operates on raw double arrays. */

/* Reduce `high_dim_pts` (n × high_dim, row-major) to `low_dim_out`
 * (n × target_dim, row-major) via Laplacian eigenmaps over a k-NN
 * graph in the high-dim space. Returns 0 on success, nonzero on
 * convergence failure. */
int laplacian_eigenmaps(const double* high_dim_pts,
                        size_t        n,
                        size_t        high_dim,
                        size_t        k_neighbors,
                        size_t        target_dim,
                        double*       low_dim_out);

#ifdef __cplusplus
}
#endif
