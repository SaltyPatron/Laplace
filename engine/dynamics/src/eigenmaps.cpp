#include "laplace/dynamics/eigenmaps.h"

#include <Eigen/Core>
#include <Eigen/Sparse>
#include <Spectra/SymEigsShiftSolver.h>
#include <Spectra/MatOp/SparseSymShiftSolve.h>
#include <Spectra/Util/SelectionRule.h>

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <limits>
#include <numeric>
#include <utility>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#include <tbb/blocked_range.h>
#include <tbb/parallel_for.h>
#endif

/* Laplacian eigenmaps (Belkin & Niyogi, "Laplacian Eigenmaps for
 * Dimensionality Reduction and Data Representation", Neural Computation
 * 15:1373-1396, 2003). Nonlinear dimensionality reduction via the spectrum
 * of the graph Laplacian - preserves local neighborhood structure.
 *
 * CANONICAL FORM. Belkin & Niyogi minimize  fᵀ L f / fᵀ D f, i.e. solve the
 * GENERALIZED eigenproblem  L f = λ D f  (NOT the unnormalized  L f = λ f).
 * We solve it through the symmetric normalized Laplacian
 *   L_sym = D^{-1/2} (D - W) D^{-1/2} = I - D^{-1/2} W D^{-1/2},
 * find its smallest eigenvectors u, and map back to the generalized
 * eigenvectors  f = D^{-1/2} u  (the embedding coordinates). This is the form
 * that converges to the Laplace-Beltrami operator of the underlying manifold;
 * the unnormalized variant is a different embedding (von Luxburg 2007).
 *
 * Pipeline:
 *   1. Build k-NN graph on high-dim points (binary weights - heat-kernel
 *      weights are a per-source tuning extension, not in v0.1).
 *   2. Symmetrize.
 *   3. Build the symmetric normalized Laplacian L_sym, regularized by ε·I so
 *      the zero eigenvalue (eigenvector D^{1/2}·1) lifts off singularity for
 *      the shift-invert factorization. L_sym's spectrum is in [0, 2], so a
 *      fixed ε is scale-correct (no degree scaling, unlike unnormalized L).
 *   4. Find the smallest (target_dim + 1) eigenpairs of L_sym via Spectra's
 *      `SymEigsShiftSolver` with shift σ = 0 (sparse LU on L_sym). Spectra's
 *      init() uses a fixed-seed residual, so the iteration is deterministic.
 *   5. In ascending-eigenvalue order: drop the smallest (the constant
 *      generalized eigenvector) and take the next `target_dim`, mapped back
 *      via f = D^{-1/2} u as the low-dim coordinates.
 *
 * Output: `low_dim_out` (n × target_dim, row-major) - row i is the
 * target_dim coordinate of the i-th point in the low-dim embedding. The
 * columns are D-orthonormal (Σ_i d_i f_i[a] f_i[b] = δ_ab) and D-weighted
 * zero-mean (Σ_i d_i f_i[k] = 0), the canonical generalized-eigenvector
 * normalization.
 *
 * Returns:
 *   0       on success.
 *   -1      null input.
 *   -2      invalid arguments (k_neighbors ≥ n, target_dim ≥ n, etc.).
 *   -3      eigensolver did not converge.
 *   -4      degenerate input (graph is too disconnected - fewer than
 *           target_dim non-trivial eigenvectors). */

namespace {

using SpMat = Eigen::SparseMatrix<double>;
using Triplet = Eigen::Triplet<double>;

/* Squared L2 distance in row-major (n × d) buffer between row a and row b. */
double sq_dist(const double* pts, std::size_t a, std::size_t b, std::size_t d) {
    double s = 0.0;
    for (std::size_t k = 0; k < d; ++k) {
        const double diff = pts[a * d + k] - pts[b * d + k];
        s += diff * diff;
    }
    return s;
}

/* Eigendecomposition core shared by laplacian_eigenmaps (k-NN graph) and
 * laplacian_eigenmaps_from_sparse_graph (precomputed adjacency). Takes a
 * symmetric non-negative adjacency `W`, builds the symmetric normalized
 * regularized Laplacian, runs Spectra's shift-invert symmetric solver, drops
 * the constant generalized eigenvector, writes `target_dim` coordinates per
 * node (f = D^{-1/2} u) into `low_dim_out` (row-major). */
int eigendecompose_laplacian(const SpMat& W,
                             std::size_t  n,
                             std::size_t  target_dim,
                             double*      low_dim_out) {
    const int ni = static_cast<int>(n);

    /* Degrees and D^{-1/2}. Isolated nodes (degree 0) get inverse-sqrt 0 -
     * they contribute no normalized coupling and land at the origin. */
    Eigen::VectorXd degrees(ni);
    degrees.setZero();
    for (int k = 0; k < W.outerSize(); ++k) {
        double row_sum = 0.0;
        for (SpMat::InnerIterator it(W, k); it; ++it) row_sum += it.value();
        degrees[k] = row_sum;
    }
    Eigen::VectorXd dinv_sqrt(ni);
    for (int i = 0; i < ni; ++i)
        dinv_sqrt[i] = (degrees[i] > 0.0) ? 1.0 / std::sqrt(degrees[i]) : 0.0;

    /* L_sym = I - D^{-1/2} W D^{-1/2} + ε·I. Off-diagonal (i,j) =
     * -W[i,j] / sqrt(d_i d_j); diagonal = 1 (for d_i>0) + ε. */
    std::vector<Triplet> l_triplets;
    l_triplets.reserve(static_cast<std::size_t>(W.nonZeros()) + n);
    for (int k = 0; k < W.outerSize(); ++k) {
        for (SpMat::InnerIterator it(W, k); it; ++it) {
            const int i = it.row(), j = it.col();
            const double v = -it.value() * dinv_sqrt[i] * dinv_sqrt[j];
            if (v != 0.0) l_triplets.emplace_back(i, j, v);
        }
    }
    const double epsilon = 1e-10;
    for (int i = 0; i < ni; ++i) {
        const double diag = (degrees[i] > 0.0 ? 1.0 : 0.0) + epsilon;
        l_triplets.emplace_back(i, i, diag);
    }

    SpMat L(ni, ni);
    L.setFromTriplets(l_triplets.begin(), l_triplets.end(),
                      [](const double& a, const double& b) { return a + b; });
    L.makeCompressed();

    const int nev = static_cast<int>(target_dim) + 1;
    const int ncv = std::min(ni - 1, std::max(2 * nev + 1, 20));
    if (ncv <= nev) return -2;

    Spectra::SparseSymShiftSolve<double> op(L);
    Spectra::SymEigsShiftSolver<Spectra::SparseSymShiftSolve<double>>
        eigs(op, nev, ncv, 0.0);
    eigs.init();
    const int nconv = eigs.compute(Spectra::SortRule::LargestMagn);
    if (eigs.info() != Spectra::CompInfo::Successful || nconv < nev) {
        return -3;
    }

    Eigen::VectorXd evals = eigs.eigenvalues();
    Eigen::MatrixXd evecs = eigs.eigenvectors();
    std::vector<int> idx(static_cast<std::size_t>(evals.size()));
    std::iota(idx.begin(), idx.end(), 0);
    std::sort(idx.begin(), idx.end(),
              [&evals](int a, int b) { return evals[a] < evals[b]; });

    if (idx.size() < target_dim + 1) return -4;
    /* Drop idx[0] (constant generalized eigenvector); embedding f = D^{-1/2} u. */
    for (std::size_t k = 0; k < target_dim; ++k) {
        const int col = idx[k + 1];
        for (std::size_t i = 0; i < n; ++i) {
            low_dim_out[i * target_dim + k] =
                evecs(static_cast<Eigen::Index>(i), col)
                * dinv_sqrt[static_cast<Eigen::Index>(i)];
        }
    }
    return 0;
}

}  // namespace

extern "C"
int laplacian_eigenmaps_from_sparse_graph(const int*    coo_rows,
                                          const int*    coo_cols,
                                          const double* coo_weights,
                                          std::size_t   nnz,
                                          std::size_t   n,
                                          std::size_t   target_dim,
                                          double*       low_dim_out) {
    if (!coo_rows || !coo_cols || !coo_weights || !low_dim_out) return -1;
    if (n == 0 || target_dim == 0) return -2;
    if (target_dim + 1 >= n) return -2;

    std::vector<Triplet> w_triplets;
    w_triplets.reserve(nnz);
    for (std::size_t e = 0; e < nnz; ++e) {
        if (coo_weights[e] <= 0.0) continue;          /* drop non-positive - substrate noise floor is 0 */
        const int r = coo_rows[e];
        const int c = coo_cols[e];
        if (r < 0 || c < 0) continue;
        if ((std::size_t)r >= n || (std::size_t)c >= n) continue;
        w_triplets.emplace_back(r, c, coo_weights[e]);
    }

    SpMat W(static_cast<int>(n), static_cast<int>(n));
    W.setFromTriplets(w_triplets.begin(), w_triplets.end(),
                      [](const double& a, const double& b){ return a + b; });

    /* Symmetrize: W = (W + Wᵀ) / 2 - sources may emit directed edges
     * (e.g., subject→object), but the Laplacian needs a symmetric adjacency. */
    SpMat WT = SpMat(W.transpose());
    SpMat Wsym = 0.5 * (W + WT);
    Wsym.makeCompressed();

    return eigendecompose_laplacian(Wsym, n, target_dim, low_dim_out);
}

extern "C"
int laplacian_eigenmaps(const double* high_dim_pts,
                        std::size_t   n,
                        std::size_t   high_dim,
                        std::size_t   k_neighbors,
                        std::size_t   target_dim,
                        double*       low_dim_out) {
    if (!high_dim_pts || !low_dim_out) return -1;
    if (n == 0 || high_dim == 0 || k_neighbors == 0 || target_dim == 0) return -2;
    if (k_neighbors >= n) return -2;
    if (target_dim + 1 >= n) return -2;  /* need at least (target_dim + 1) eigenpairs distinct from n */

    /* --- 1. Build k-NN graph (binary weights). For each point i, find the
     * k_neighbors smallest squared distances to the other points.
     *
     * Each point i is independent: it reads the shared const point buffer and
     * writes EXACTLY k_neighbors triplets to a fixed slot [i*k, (i+1)*k). So the
     * outer loop parallelizes with oneTBB (when linked) with no contention, and
     * the result is BIT-IDENTICAL to the serial build — the triplet vector ends
     * up in the same order, every sq_dist sums in the same order regardless of
     * thread, and the per-i partial_sort is unchanged. The `dists` scratch is
     * per-i (thread-local), never shared. This replaces the O(n²·d) SINGLE-
     * THREADED k-NN that made a large-vocab embedding morph effectively never
     * finish; determinism (RULES.md R7) is preserved because no cross-thread
     * reduction is introduced — the distance sums and the eigensolver are
     * untouched. A serial fallback covers the Eigen-only (no-TBB) build. */
    std::vector<Triplet> w_triplets(n * k_neighbors);

    auto build_knn_row = [&](std::size_t i) {
        std::vector<std::pair<double, std::size_t>> dists(n);
        for (std::size_t j = 0; j < n; ++j) {
            dists[j] = {(i == j) ? std::numeric_limits<double>::infinity()
                                 : sq_dist(high_dim_pts, i, j, high_dim),
                        j};
        }
        std::partial_sort(dists.begin(),
                          dists.begin() + static_cast<std::ptrdiff_t>(k_neighbors),
                          dists.end(),
                          [](const auto& a, const auto& b) { return a.first < b.first; });
        for (std::size_t k = 0; k < k_neighbors; ++k) {
            w_triplets[i * k_neighbors + k] =
                Triplet(static_cast<int>(i),
                        static_cast<int>(dists[k].second), 1.0);
        }
    };

#ifdef LAPLACE_HAS_MKL
    tbb::parallel_for(tbb::blocked_range<std::size_t>(0, n),
                      [&](const tbb::blocked_range<std::size_t>& range) {
                          for (std::size_t i = range.begin(); i != range.end(); ++i)
                              build_knn_row(i);
                      });
#else
    for (std::size_t i = 0; i < n; ++i) build_knn_row(i);
#endif

    SpMat W_raw(static_cast<int>(n), static_cast<int>(n));
    W_raw.setFromTriplets(w_triplets.begin(), w_triplets.end(),
                          [](const double& a, const double& b) { (void)b; return a; });

    /* --- 2. Symmetrize: W[i,j] = max(W_raw[i,j], W_raw[j,i]). Binary,
     * so element-wise max is OR (an edge is kept if it's in either
     * direction's top-k). ---------------------------------------------- */
    SpMat W = W_raw + Eigen::SparseMatrix<double>(W_raw.transpose());
    for (int k = 0; k < W.outerSize(); ++k) {
        for (SpMat::InnerIterator it(W, k); it; ++it) {
            it.valueRef() = (it.value() > 0.0) ? 1.0 : 0.0;
        }
    }
    W.makeCompressed();

    /* --- 3-5. Symmetric normalized Laplacian eigendecomposition (shared
     * with the sparse-graph entry point - one canonical implementation). */
    return eigendecompose_laplacian(W, n, target_dim, low_dim_out);
}
