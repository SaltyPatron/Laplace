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

/* Same eigendecomposition pipeline as `laplacian_eigenmaps` but takes a
 * precomputed sparse adjacency in COO triples (substrate's typed-edge
 * attestation set, weighted by Glicko-2 effective μ per kind × kind-value
 * tier × source-trust class). Skips the k-NN construction step.
 *
 *   coo_rows / coo_cols : length nnz; index range [0, n).
 *   coo_weights         : length nnz; positive edge weights (Glicko-2
 *                         effective μ scaled to a meaningful magnitude;
 *                         negative weights are clipped to zero internally
 *                         since the Laplacian construction requires
 *                         non-negative adjacency).
 *   nnz                  : number of triples (may include duplicate
 *                         (row,col) pairs — they're summed).
 *   n                    : number of nodes (vocab size).
 *   target_dim           : output dimension (top eigenvectors of L,
 *                         skipping the constant-function eigenvector).
 *   low_dim_out          : n × target_dim, row-major.
 *
 * Returns:
 *   0   on success.
 *   -1  null input.
 *   -2  invalid arguments (n == 0, target_dim + 1 >= n, etc.).
 *   -3  eigensolver did not converge.
 *   -4  degenerate input (graph too disconnected). */
int laplacian_eigenmaps_from_sparse_graph(const int*    coo_rows,
                                          const int*    coo_cols,
                                          const double* coo_weights,
                                          size_t        nnz,
                                          size_t        n,
                                          size_t        target_dim,
                                          double*       low_dim_out);

#ifdef __cplusplus
}
#endif
