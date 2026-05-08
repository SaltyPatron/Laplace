/*
 * knn_exact.h — KnnExactService public API.
 *
 * Phase 2 / Track B / Service B15.
 *
 * Exact KNN by cosine similarity over double-precision dense matrices via
 * MKL-tiled brute-force GEMM. Per CLAUDE.md banned patterns: HNSW or any
 * other approximate KNN is forbidden — substrate inference uses exact
 * cosine similarity, period.
 *
 * Two operating modes:
 *   - laplace_knn_exact_cosine_d: query × dictionary brute-force GEMM,
 *     returning top-k dictionary indices per query by descending cosine.
 *   - laplace_knn_self_cosine_d: dictionary × dictionary self-similarity
 *     (used to build sparse Laplacians for B17 LaplacianEigenmap), with
 *     self-edges (i == j) excluded.
 *
 * Both functions L2-normalize their input matrices internally (working on
 * scratch copies — caller's matrices are untouched). Output similarity
 * values are in [-1, 1].
 *
 * Memory: O(n_queries × n_dict) doubles for the similarity matrix when
 * either dimension fits in RAM. The "MKL-tiled" qualifier in the synthesis
 * doc means tile the GEMM into chunks that fit cache; for vocabulary-scale
 * dictionaries (V ≤ 200K, D ≤ 4096) the full matrix fits comfortably.
 */

#ifndef LAPLACE_KNN_EXACT_H
#define LAPLACE_KNN_EXACT_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Compute top-k cosine-similarity nearest neighbors of each query row in
 * the dictionary.
 *
 * Inputs:
 *   queries        — n_queries × dim, row-major, double precision.
 *   n_queries      — number of query rows.
 *   dictionary     — n_dict × dim, row-major, double precision.
 *   n_dict         — number of dictionary rows.
 *   dim            — column count of both matrices (must match).
 *   k              — number of nearest neighbors per query (1 <= k <= n_dict).
 *
 * Outputs:
 *   out_indices    — n_queries × k integer matrix, row-major. Entry [q, j]
 *                    is the index into the dictionary of the j-th nearest
 *                    neighbor of query q (0-based, sorted by descending
 *                    cosine similarity).
 *   out_similarities — n_queries × k double matrix, row-major. Entry
 *                      [q, j] is the cosine similarity in [-1, 1].
 *
 * Returns 0 on success, nonzero on error (out of memory, invalid arguments).
 */
int laplace_knn_exact_cosine_d(
    const double *queries,
    int           n_queries,
    const double *dictionary,
    int           n_dict,
    int           dim,
    int           k,
    int          *out_indices,
    double       *out_similarities);

/*
 * Self-similarity variant: dictionary × dictionary KNN with i == j edges
 * excluded. Used to build the symmetric sparse Laplacian fed into
 * B17 LaplacianEigenmap. Output is n_dict × k for both indices and
 * similarities (each row's diagonal is filtered out before top-k).
 *
 * Returns 0 on success, nonzero on error.
 */
int laplace_knn_self_cosine_d(
    const double *dictionary,
    int           n_dict,
    int           dim,
    int           k,
    int          *out_indices,
    double       *out_similarities);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_KNN_EXACT_H */
