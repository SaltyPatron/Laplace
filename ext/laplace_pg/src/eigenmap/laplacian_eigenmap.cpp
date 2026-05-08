/*
 * laplacian_eigenmap.cpp — symmetric normalized Laplacian eigenmap with
 * Spectra leading-k eigenpairs + S^3 projection.
 *
 * The flow:
 *   1. Build sparse W (n × n) by symmetrizing the KNN graph: W[i,j] = sim
 *      iff j ∈ KNN(i) OR i ∈ KNN(j), with negative sims clamped to 0.
 *   2. Compute degrees D[i] = sum_j W[i,j].
 *   3. Build the symmetric normalized Laplacian L = I - D^{-1/2} W D^{-1/2}.
 *   4. Use Spectra SymEigsShiftSolver in shift-and-invert mode around
 *      sigma = 0 to extract the smallest (output_dim + 1) eigenpairs.
 *   5. Drop the trivial eigenvector (constant, eigenvalue 0).
 *   6. The remaining output_dim eigenvectors form the n × output_dim embedding.
 *   7. Per-row L2-normalize so each row lands on the unit (output_dim − 1)-sphere.
 *
 * Spectra requires C++17+; this file is C++ but exposes a pure-C API so
 * callers (the rest of laplace_native, P/Invoke from managed) link uniformly.
 */

#include "laplace_pg/eigenmap.h"

#include <Eigen/Sparse>
#include <Eigen/Dense>
#include <Spectra/SymEigsSolver.h>
#include <Spectra/MatOp/SparseSymMatProd.h>

#include <algorithm>
#include <cmath>
#include <unordered_set>
#include <vector>

namespace {

/// Pack (row, col) into a 64-bit key for unordered_set dedup of edges
/// (i, j) where i < j (we only store the upper triangle and let
/// SparseMatrix's symmetric flag mirror it).
static inline uint64_t edge_key(int a, int b) {
    return (static_cast<uint64_t>(a) << 32) | static_cast<uint32_t>(b);
}

}  // namespace

extern "C" int laplace_laplacian_eigenmap_s3_d(
    const int    *knn_indices,
    const double *knn_similarities,
    int           n,
    int           k_neighbors,
    int           output_dim,
    double       *out_embedding)
{
    if (knn_indices == nullptr || knn_similarities == nullptr ||
        out_embedding == nullptr) { return 1; }
    if (n <= 1 || k_neighbors <= 0 || output_dim <= 0) { return 1; }
    if (output_dim >= n) { return 1; }
    /* Spectra requires ncv > nev, and nev <= n - 1; we'll take n_eig
     * eigenpairs where n_eig = output_dim + 1 (drop the trivial mode). */
    const int n_eig = output_dim + 1;
    if (n_eig >= n) { return 1; }

    /* Step 1: build symmetric weighted adjacency W as a sparse matrix.
     * Use a triplet list, then build SparseMatrix in column-major.
     * Symmetrize by inserting both (i,j) and (j,i) in the triplet; the
     * SparseMatrix will sum collisions if i appears in j's KNN AND vice
     * versa — that doubles the weight, which is fine because Laplacian
     * eigenmap is invariant under positive global scaling of W. */
    using Triplet = Eigen::Triplet<double>;
    std::vector<Triplet> triplets;
    triplets.reserve(static_cast<size_t>(n) * static_cast<size_t>(k_neighbors) * 2);

    for (int i = 0; i < n; ++i) {
        const int    *idx_row = knn_indices + static_cast<size_t>(i) * k_neighbors;
        const double *sim_row = knn_similarities + static_cast<size_t>(i) * k_neighbors;
        for (int kk = 0; kk < k_neighbors; ++kk) {
            const int    j = idx_row[kk];
            const double s = sim_row[kk];
            if (j < 0 || j >= n || j == i) { continue; }
            const double w = s > 0.0 ? s : 0.0;
            if (w == 0.0) { continue; }
            triplets.emplace_back(i, j, w);
            triplets.emplace_back(j, i, w);
        }
    }

    Eigen::SparseMatrix<double> W(n, n);
    W.setFromTriplets(triplets.begin(), triplets.end(),
                      [](double a, double b) { return std::max(a, b); });

    /* Step 2: degrees D[i] = sum_j W[i,j]. */
    Eigen::VectorXd D = Eigen::VectorXd::Zero(n);
    for (Eigen::Index col = 0; col < W.outerSize(); ++col) {
        for (Eigen::SparseMatrix<double>::InnerIterator it(W, col); it; ++it) {
            D[it.row()] += it.value();
        }
    }

    /* Replace zero degrees with 1 to avoid division by zero (isolated
     * nodes contribute 0 to L = I - D^{-1/2} W D^{-1/2} anyway). */
    for (int i = 0; i < n; ++i) { if (D[i] <= 0.0) { D[i] = 1.0; } }

    Eigen::VectorXd Dinvsqrt(n);
    for (int i = 0; i < n; ++i) { Dinvsqrt[i] = 1.0 / std::sqrt(D[i]); }

    /* Step 3: build M = D^{-1/2} W D^{-1/2}. We compute the eigendecomp
     * of M (largest eigenvalues correspond to smallest eigenvalues of L
     * = I - M, since lambda(L) = 1 - lambda(M)). This avoids the
     * shift-and-invert dance and uses the simpler Spectra SymEigsSolver. */
    /* Scale W to M. */
    Eigen::SparseMatrix<double> M(n, n);
    {
        std::vector<Triplet> mtriplets;
        mtriplets.reserve(static_cast<size_t>(W.nonZeros()));
        for (Eigen::Index col = 0; col < W.outerSize(); ++col) {
            for (Eigen::SparseMatrix<double>::InnerIterator it(W, col); it; ++it) {
                const Eigen::Index r = it.row();
                const Eigen::Index c = it.col();
                const double       v = it.value() * Dinvsqrt[r] * Dinvsqrt[c];
                mtriplets.emplace_back(static_cast<int>(r), static_cast<int>(c), v);
            }
        }
        M.setFromTriplets(mtriplets.begin(), mtriplets.end());
    }

    /* Step 4: largest n_eig eigenpairs of M via Spectra. Convergence
     * speed depends on ncv; recommend 2*nev <= ncv <= n. */
    Spectra::SparseSymMatProd<double> op(M);
    int ncv = std::min(n, std::max(2 * n_eig + 1, 20));

    Spectra::SymEigsSolver<Spectra::SparseSymMatProd<double>>
        eigs(op, n_eig, ncv);

    eigs.init();
    const int n_iter = 1000;
    eigs.compute(Spectra::SortRule::LargestAlge, n_iter, 1e-10,
                 Spectra::SortRule::LargestAlge);

    if (eigs.info() != Spectra::CompInfo::Successful) {
        /* Convergence failure — return error. Caller can retry with a
         * different k_neighbors or n_iter. */
        return 2;
    }

    Eigen::MatrixXd vectors = eigs.eigenvectors();   /* n × n_eig */
    /* Spectra returns columns sorted by descending eigenvalue (LargestAlge).
     * The trivial eigenvalue λ(M) = 1 corresponds to λ(L) = 0 — that's the
     * first column. Drop it. The next output_dim columns are the embedding. */
    if (vectors.cols() < n_eig) { return 3; }

    Eigen::MatrixXd embedding = vectors.middleCols(1, output_dim);

    /* Step 5: per-row L2-normalize to S^(output_dim - 1). */
    for (int i = 0; i < n; ++i) {
        double sum = 0.0;
        for (int d = 0; d < output_dim; ++d) {
            const double v = embedding(i, d);
            sum += v * v;
        }
        const double r = std::sqrt(sum);
        if (r > 0.0 && std::isfinite(r)) {
            const double inv = 1.0 / r;
            for (int d = 0; d < output_dim; ++d) {
                embedding(i, d) *= inv;
            }
        }
    }

    /* Step 6: write to row-major output_embedding. */
    for (int i = 0; i < n; ++i) {
        for (int d = 0; d < output_dim; ++d) {
            out_embedding[static_cast<size_t>(i) * output_dim + d] = embedding(i, d);
        }
    }
    return 0;
}
