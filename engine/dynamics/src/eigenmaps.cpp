#include "laplace/dynamics/eigenmaps.h"

#include <Eigen/Core>
#include <Eigen/Sparse>
#include <Spectra/SymEigsShiftSolver.h>
#include <Spectra/MatOp/SparseSymShiftSolve.h>
#include <Spectra/Util/SelectionRule.h>

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <limits>
#include <numeric>
#include <utility>
#include <vector>

/* Laplacian eigenmaps (Belkin & Niyogi, "Laplacian Eigenmaps for
 * Dimensionality Reduction and Data Representation", Neural Computation
 * 15:1373–1396, 2003). Nonlinear dimensionality reduction via the spectrum
 * of the graph Laplacian — preserves local neighborhood structure.
 *
 * Pipeline:
 *   1. Build k-NN graph on high-dim points (binary weights — heat-kernel
 *      weights are a per-source tuning extension, not in v0.1).
 *   2. Symmetrize via W = max(W, Wᵀ) (an edge is kept if it's in either
 *      direction's top-k).
 *   3. Build degree matrix D and unnormalized Laplacian L = D − W as a
 *      sparse symmetric matrix.
 *   4. Regularize: L_reg = L + ε·I where ε ≪ smallest meaningful
 *      eigenvalue. This avoids singularity (L has a zero eigenvalue on
 *      every connected component — the constant function) so the
 *      shift-invert factorization is non-singular.
 *   5. Find smallest (target_dim + 1) eigenpairs of L_reg via Spectra's
 *      `SymEigsShiftSolver` with shift σ = 0 (uses sparse LU on L_reg).
 *      In ascending-eigenvalue order: drop the smallest (the regularized
 *      zero-eigenvalue eigenvector, which is constant up to numerical
 *      noise) and take the next `target_dim` eigenvectors as the
 *      coordinates of the low-dim embedding.
 *
 * Sparse symmetric eigensolver: Spectra's `SymEigsShiftSolver` over
 * `SparseSymShiftSolve` factorizes `L_reg − σ·I = L_reg` once via Eigen's
 * sparse LU, then applies it implicitly through Lanczos iteration. This is
 * the ARPACK-equivalent path for sparse symmetric eigenproblems.
 *
 * Output: `low_dim_out` (n × target_dim, row-major) — row i is the
 * target_dim coordinate of the i-th point in the low-dim embedding.
 *
 * Returns:
 *   0       on success.
 *   -1      null input.
 *   -2      invalid arguments (k_neighbors ≥ n, target_dim ≥ n, etc.).
 *   -3      eigensolver did not converge.
 *   -4      degenerate input (graph is too disconnected — fewer than
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

}  // namespace

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

    /* --- 1. Build k-NN graph (binary weights). For each point i, find
     * the k_neighbors smallest squared distances to other points. ----- */
    std::vector<Triplet> w_triplets;
    w_triplets.reserve(n * k_neighbors * 2);  /* upper bound for symmetrization */

    std::vector<std::pair<double, std::size_t>> dists(n);
    for (std::size_t i = 0; i < n; ++i) {
        for (std::size_t j = 0; j < n; ++j) {
            dists[j] = {(i == j) ? std::numeric_limits<double>::infinity()
                                 : sq_dist(high_dim_pts, i, j, high_dim),
                        j};
        }
        std::partial_sort(dists.begin(), dists.begin() + static_cast<std::ptrdiff_t>(k_neighbors),
                          dists.end(),
                          [](const auto& a, const auto& b) { return a.first < b.first; });
        for (std::size_t k = 0; k < k_neighbors; ++k) {
            w_triplets.emplace_back(static_cast<int>(i),
                                    static_cast<int>(dists[k].second), 1.0);
        }
    }

    SpMat W_raw(static_cast<int>(n), static_cast<int>(n));
    W_raw.setFromTriplets(w_triplets.begin(), w_triplets.end(),
                          [](const double& a, const double& b) { (void)b; return a; });

    /* --- 2. Symmetrize: W[i,j] = max(W_raw[i,j], W_raw[j,i]). Binary,
     * so element-wise max is OR. ---------------------------------------- */
    SpMat W = W_raw + Eigen::SparseMatrix<double>(W_raw.transpose());
    /* +0 then divide by ≥1 fixes the case where edge appears in both
     * directions (counted twice → 2; divide by max to get 1). */
    for (int k = 0; k < W.outerSize(); ++k) {
        for (SpMat::InnerIterator it(W, k); it; ++it) {
            it.valueRef() = (it.value() > 0.0) ? 1.0 : 0.0;
        }
    }

    /* --- 3. Unnormalized Laplacian L = D − W. ------------------------- */
    Eigen::VectorXd degrees(static_cast<int>(n));
    degrees.setZero();
    for (int k = 0; k < W.outerSize(); ++k) {
        double row_sum = 0.0;
        for (SpMat::InnerIterator it(W, k); it; ++it) row_sum += it.value();
        degrees[k] = row_sum;
    }

    std::vector<Triplet> l_triplets;
    l_triplets.reserve(static_cast<std::size_t>(W.nonZeros()) + n);
    for (int k = 0; k < W.outerSize(); ++k) {
        for (SpMat::InnerIterator it(W, k); it; ++it) {
            l_triplets.emplace_back(it.row(), it.col(), -it.value());
        }
    }
    /* --- 4. Add diagonal D + ε·I regularization. ε scaled to the
     * largest degree so it's negligible vs the non-trivial spectrum but
     * large enough to lift the zero eigenvalue away from singularity. - */
    const double max_deg = degrees.maxCoeff();
    const double epsilon = (max_deg > 0.0)
        ? max_deg * 1e-10
        : 1e-10;
    for (int i = 0; i < static_cast<int>(n); ++i) {
        l_triplets.emplace_back(i, i, degrees[i] + epsilon);
    }

    SpMat L(static_cast<int>(n), static_cast<int>(n));
    L.setFromTriplets(l_triplets.begin(), l_triplets.end());
    L.makeCompressed();

    /* --- 5. Spectra smallest-eigenvalues via shift-invert at σ = 0. ---
     * Number of eigenvalues to compute: target_dim + 1 (we'll drop the
     * smallest one, which corresponds to the regularized zero
     * eigenvalue). The Krylov subspace size `ncv` must satisfy
     * 2*nev ≤ ncv ≤ n; pick the recommended max(2*nev + 1, 20). */
    const int nev = static_cast<int>(target_dim) + 1;
    const int ncv = std::min(static_cast<int>(n) - 1,
                             std::max(2 * nev + 1, 20));
    if (ncv <= nev) return -2;

    Spectra::SparseSymShiftSolve<double> op(L);
    Spectra::SymEigsShiftSolver<Spectra::SparseSymShiftSolve<double>>
        eigs(op, nev, ncv, 0.0);
    eigs.init();
    const int nconv = eigs.compute(Spectra::SortRule::LargestMagn);
    if (eigs.info() != Spectra::CompInfo::Successful || nconv < nev) {
        return -3;
    }

    /* Eigenvalues are returned sorted by the sort rule, but the natural
     * order we want is ascending in the ORIGINAL Laplacian's spectrum.
     * Sort by eigenvalue ascending. */
    Eigen::VectorXd evals = eigs.eigenvalues();
    Eigen::MatrixXd evecs = eigs.eigenvectors();
    std::vector<int> idx(static_cast<std::size_t>(evals.size()));
    std::iota(idx.begin(), idx.end(), 0);
    std::sort(idx.begin(), idx.end(),
              [&evals](int a, int b) { return evals[a] < evals[b]; });

    /* Drop idx[0] (the regularized constant-function eigenvector), take
     * next target_dim. Each eigenvector has length n; place coord k of
     * each point i into low_dim_out[i*target_dim + k]. */
    if (idx.size() < target_dim + 1) return -4;
    for (std::size_t k = 0; k < target_dim; ++k) {
        const int col = idx[k + 1];
        for (std::size_t i = 0; i < n; ++i) {
            low_dim_out[i * target_dim + k] = evecs(static_cast<Eigen::Index>(i), col);
        }
    }
    return 0;
}
