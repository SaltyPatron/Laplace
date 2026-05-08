/*
 * eigenmap.h — LaplacianEigenmapService public API.
 *
 * Phase 2 / Track B / Service B17.
 *
 * Per the synthesis doc + CLAUDE.md invariant 2: AI model fireflies are
 * extracted by computing the leading eigenvectors of the symmetric
 * normalized graph Laplacian over a k-NN cosine-similarity graph among
 * embedding rows, then Gram-Schmidt + S^3 projection per row.
 *
 *   1. Input: an n × k_neighbors KNN graph (indices + cosine similarities)
 *      built by B15 KnnExactService.
 *   2. Build the symmetric weighted adjacency W[i,j] = W[j,i] = sim where
 *      either i is in j's KNN or j is in i's KNN; otherwise 0.
 *   3. Degree D[i] = sum_j W[i,j].
 *   4. Symmetric normalized Laplacian L = I - D^{-1/2} W D^{-1/2}.
 *   5. Find the smallest (output_dim + 1) eigenpairs via Spectra
 *      SymEigsShiftSolver (constant-zero eigenvalue first, drop it).
 *   6. The n × output_dim embedding = the next output_dim eigenvectors.
 *   7. (Optionally) Gram-Schmidt re-orthonormalize columns.
 *   8. Per-row L2-normalize → unit vectors on S^(output_dim - 1).
 *
 * For substrate fireflies on S^3, output_dim = 4. Each row's resulting
 * 4-vector is the per-token firefly position, content-bound to its
 * substrate text entity hash by the caller.
 */

#ifndef LAPLACE_EIGENMAP_H
#define LAPLACE_EIGENMAP_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Compute the Laplacian eigenmap embedding from a precomputed cosine
 * KNN graph and project each output row to the unit (output_dim - 1)-sphere.
 *
 * Inputs:
 *   knn_indices        — n × k_neighbors row-major. Entry [i, j] is the
 *                        index in [0, n) of the j-th nearest neighbor of i.
 *                        Self-loops MUST already be excluded.
 *   knn_similarities   — n × k_neighbors row-major. Cosine similarities
 *                        in [-1, 1]. Negative similarities are treated as
 *                        weight 0 (Laplacian eigenmaps require non-negative
 *                        edge weights).
 *   n                  — number of rows (and number of nodes in the graph).
 *   k_neighbors        — KNN graph degree per row.
 *   output_dim         — target embedding dimension (4 for substrate S^3).
 *
 * Outputs:
 *   out_embedding      — n × output_dim row-major. Each row is a unit
 *                        vector on the output_dim-sphere.
 *
 * Returns 0 on success, nonzero on error. Errors include: invalid args,
 * out-of-memory, eigensolver convergence failure (the implementation falls
 * back to a regularized solve once before giving up).
 */
int laplace_laplacian_eigenmap_s3_d(
    const int    *knn_indices,
    const double *knn_similarities,
    int           n,
    int           k_neighbors,
    int           output_dim,
    double       *out_embedding);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_EIGENMAP_H */
