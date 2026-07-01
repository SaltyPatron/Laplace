#include "laplace/dynamics/eigenmaps.h"

#include <Eigen/Core>
#include <Eigen/Sparse>
#include <Spectra/SymEigsSolver.h>
#include <Spectra/MatOp/SparseSymMatProd.h>
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
#include "laplace/dynamics/tbb_parallel.h"
#include <oneapi/tbb/blocked_range.h>
#endif

namespace {

using SpMat = Eigen::SparseMatrix<double>;
using Triplet = Eigen::Triplet<double>;

#if defined(__AVX2__) && defined(__x86_64__)
#  include <immintrin.h>
#endif

double sq_dist(const double* pts, std::size_t a, std::size_t b, std::size_t d) {
    const double* pa = pts + a * d;
    const double* pb = pts + b * d;
    double s = 0.0;
    std::size_t k = 0;
#if defined(__AVX2__) && defined(__x86_64__)
    __m256d acc = _mm256_setzero_pd();
    for (; k + 4 <= d; k += 4) {
        __m256d va = _mm256_loadu_pd(pa + k);
        __m256d vb = _mm256_loadu_pd(pb + k);
        __m256d diff = _mm256_sub_pd(va, vb);
        acc = _mm256_fmadd_pd(diff, diff, acc);
    }
    alignas(32) double parts[4];
    _mm256_store_pd(parts, acc);
    s = parts[0] + parts[1] + parts[2] + parts[3];
#endif
    for (; k < d; ++k) {
        const double diff = pa[k] - pb[k];
        s += diff * diff;
    }
    return s;
}

int eigendecompose_laplacian(const SpMat& W,
                             std::size_t  n,
                             std::size_t  target_dim,
                             double*      low_dim_out) {
    const int ni = static_cast<int>(n);

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

    
    
    
    Spectra::SparseSymMatProd<double> op(L);
    Spectra::SymEigsSolver<Spectra::SparseSymMatProd<double>>
        eigs(op, nev, ncv);
    eigs.init();
    const int nconv = eigs.compute(Spectra::SortRule::SmallestAlge);
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

}

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
        const double w = std::fabs(coo_weights[e]);
        if (w == 0.0) continue;
        const int r = coo_rows[e];
        const int c = coo_cols[e];
        if (r < 0 || c < 0) continue;
        if ((std::size_t)r >= n || (std::size_t)c >= n) continue;
        w_triplets.emplace_back(r, c, w);
    }

    SpMat W(static_cast<int>(n), static_cast<int>(n));
    W.setFromTriplets(w_triplets.begin(), w_triplets.end(),
                      [](const double& a, const double& b){ return a + b; });

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
    if (target_dim + 1 >= n) return -2;

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
    laplace::tbb_ops::parallel_for(
        oneapi::tbb::blocked_range<std::size_t>(0, n),
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

    SpMat W = W_raw + Eigen::SparseMatrix<double>(W_raw.transpose());
    for (int k = 0; k < W.outerSize(); ++k) {
        for (SpMat::InnerIterator it(W, k); it; ++it) {
            it.valueRef() = (it.value() > 0.0) ? 1.0 : 0.0;
        }
    }
    W.makeCompressed();

    return eigendecompose_laplacian(W, n, target_dim, low_dim_out);
}
