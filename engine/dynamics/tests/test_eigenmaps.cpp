#include <gtest/gtest.h>

#include <Eigen/Core>
#include <cmath>
#include <random>
#include <vector>

#include "laplace/dynamics/eigenmaps.h"

/* Laplacian eigenmaps (Belkin & Niyogi 2003) via Spectra sparse
 * eigensolver. Verifies the algebraic properties + a manifold-recovery
 * sanity test (points on a 1D ring in high-dim space should embed to a
 * 1D circle in 2D output). */

namespace {

bool embedding_preserves_order_on_ring(const double* low, std::size_t n,
                                       std::size_t target_dim) {
    /* For a ring with N points, a successful 2D Laplacian embedding
     * recovers the cyclic order (rotation of the input ring) up to:
     *   - reflection (the 2nd vs 3rd eigenvector ordering can swap signs)
     *   - global rotation
     *   - direction (CW vs CCW)
     * The verifiable property: sort points by angle in the output; the
     * resulting order is a cyclic rotation of the input order (modulo
     * reflection). We measure success by: each input point's index
     * neighbors-on-ring map to angular neighbors in the embedding (within
     * a window of K=3). */
    if (target_dim < 2) return false;
    std::vector<std::pair<double, std::size_t>> angles(n);
    for (std::size_t i = 0; i < n; ++i) {
        angles[i] = {std::atan2(low[i * target_dim + 1], low[i * target_dim + 0]), i};
    }
    std::sort(angles.begin(), angles.end());

    /* For each input point i, find its position in the sorted-by-angle
     * order. Adjacent input indices should map to adjacent angular
     * positions (within a small window). */
    std::vector<std::size_t> rank(n);
    for (std::size_t p = 0; p < n; ++p) rank[angles[p].second] = p;

    int adjacent_hits = 0;
    for (std::size_t i = 0; i < n; ++i) {
        const std::size_t next_i = (i + 1) % n;
        const std::size_t d = (rank[next_i] + n - rank[i]) % n;
        const std::size_t d_back = (rank[i] + n - rank[next_i]) % n;
        const std::size_t dist = std::min(d, d_back);
        if (dist <= 2) ++adjacent_hits;
    }
    return adjacent_hits >= static_cast<int>(n * 9 / 10);
}

}  // namespace

TEST(LaplaceDynamicsEigenmaps, RejectsNullInputs) {
    double out[16];
    EXPECT_EQ(-1, laplacian_eigenmaps(nullptr, 4, 3, 2, 4, out));
    double pts[12] = {0};
    EXPECT_EQ(-1, laplacian_eigenmaps(pts, 4, 3, 2, 4, nullptr));
}

TEST(LaplaceDynamicsEigenmaps, RejectsInvalidArgs) {
    double pts[12] = {0};
    double out[16];
    /* k_neighbors >= n */
    EXPECT_EQ(-2, laplacian_eigenmaps(pts, 4, 3, 5, 2, out));
    /* target_dim + 1 >= n */
    EXPECT_EQ(-2, laplacian_eigenmaps(pts, 4, 3, 2, 4, out));
    /* n=0 */
    EXPECT_EQ(-2, laplacian_eigenmaps(pts, 0, 3, 2, 4, out));
}

TEST(LaplaceDynamicsEigenmaps, RecoversRingManifold) {
    /* N points on a 1D ring embedded in 10D space (last 8 coords are
     * small noise). The Laplacian embedding to 2D should recover the
     * circular structure. */
    constexpr std::size_t N = 60;
    constexpr std::size_t HIGH_DIM = 10;
    constexpr std::size_t K = 4;
    constexpr std::size_t TARGET = 2;

    std::mt19937_64 rng(0xC0FFEEULL);
    std::normal_distribution<double> noise(0.0, 1e-3);

    std::vector<double> pts(N * HIGH_DIM, 0.0);
    for (std::size_t i = 0; i < N; ++i) {
        const double theta = 2.0 * M_PI * static_cast<double>(i) / static_cast<double>(N);
        pts[i * HIGH_DIM + 0] = std::cos(theta);
        pts[i * HIGH_DIM + 1] = std::sin(theta);
        for (std::size_t k = 2; k < HIGH_DIM; ++k) {
            pts[i * HIGH_DIM + k] = noise(rng);
        }
    }

    std::vector<double> emb(N * TARGET);
    ASSERT_EQ(0, laplacian_eigenmaps(pts.data(), N, HIGH_DIM, K, TARGET, emb.data()));
    EXPECT_TRUE(embedding_preserves_order_on_ring(emb.data(), N, TARGET))
        << "ring structure not recovered in 2D embedding";
}

/* Build a symmetric path graph 0-1-...-(N-1) as COO (both directions, unit
 * weight) + its degrees. Non-uniform degrees (ends=1, interior=2) so the
 * D-weighting is actually exercised. */
static void make_path_graph(int N, std::vector<int>& rows, std::vector<int>& cols,
                            std::vector<double>& w, std::vector<double>& deg) {
    rows.clear(); cols.clear(); w.clear(); deg.assign(static_cast<std::size_t>(N), 0.0);
    for (int i = 0; i + 1 < N; ++i) {
        rows.push_back(i);     cols.push_back(i + 1); w.push_back(1.0);
        rows.push_back(i + 1); cols.push_back(i);     w.push_back(1.0);
    }
    for (std::size_t e = 0; e < rows.size(); ++e) deg[static_cast<std::size_t>(rows[e])] += w[e];
}

TEST(LaplaceDynamicsEigenmaps, EmbeddingIsDWeightedZeroMean) {
    /* CANONICAL (Belkin-Niyogi generalized) eigenvectors are D-orthogonal to
     * the constant eigenvector, so each output column has DEGREE-WEIGHTED mean
     * zero: Σ_i d_i f_i[k] = 0 (NOT the plain mean). Use the sparse-graph entry
     * point so the test controls the graph and knows the degrees. */
    constexpr int N = 40;
    constexpr std::size_t TARGET = 3;
    std::vector<int> rows, cols; std::vector<double> w, deg;
    make_path_graph(N, rows, cols, w, deg);

    std::vector<double> emb(static_cast<std::size_t>(N) * TARGET);
    ASSERT_EQ(0, laplacian_eigenmaps_from_sparse_graph(
        rows.data(), cols.data(), w.data(), rows.size(),
        static_cast<std::size_t>(N), TARGET, emb.data()));

    for (std::size_t k = 0; k < TARGET; ++k) {
        double wmean = 0.0;
        for (int i = 0; i < N; ++i) wmean += deg[static_cast<std::size_t>(i)] * emb[i * TARGET + k];
        EXPECT_NEAR(wmean, 0.0, 1e-7)
            << "dim " << k << " not degree-weighted zero-mean (Σ d_i f_i = " << wmean << ")";
    }
}

TEST(LaplaceDynamicsEigenmaps, EmbeddingColumnsAreDOrthonormal) {
    /* CANONICAL generalized eigenvectors are D-ORTHONORMAL:
     * Σ_i d_i f_i[a] f_i[b] = δ_ab. (Equivalently the underlying L_sym
     * eigenvectors u = D^{1/2} f are plain-orthonormal, which Spectra
     * returns to ~1e-12; the D-form is the embedding's invariant.) */
    constexpr int N = 50;
    constexpr std::size_t TARGET = 4;
    std::vector<int> rows, cols; std::vector<double> w, deg;
    make_path_graph(N, rows, cols, w, deg);

    std::vector<double> emb(static_cast<std::size_t>(N) * TARGET);
    ASSERT_EQ(0, laplacian_eigenmaps_from_sparse_graph(
        rows.data(), cols.data(), w.data(), rows.size(),
        static_cast<std::size_t>(N), TARGET, emb.data()));

    for (std::size_t a = 0; a < TARGET; ++a) {
        double naa = 0.0;
        for (int i = 0; i < N; ++i) {
            const double f = emb[i * TARGET + a];
            naa += deg[static_cast<std::size_t>(i)] * f * f;
        }
        EXPECT_NEAR(naa, 1.0, 1e-7) << "column " << a << " D-norm² = " << naa;

        for (std::size_t b = a + 1; b < TARGET; ++b) {
            double nab = 0.0;
            for (int i = 0; i < N; ++i)
                nab += deg[static_cast<std::size_t>(i)] * emb[i * TARGET + a] * emb[i * TARGET + b];
            EXPECT_NEAR(nab, 0.0, 1e-7)
                << "columns " << a << " and " << b << " not D-orthogonal (D-dot=" << nab << ")";
        }
    }
}

TEST(LaplaceDynamicsEigenmaps, DeterministicOnIdenticalInput) {
    constexpr std::size_t N = 50;
    constexpr std::size_t HIGH_DIM = 6;
    constexpr std::size_t K = 5;
    constexpr std::size_t TARGET = 3;

    std::mt19937_64 rng(0xABCDULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);
    std::vector<double> pts(N * HIGH_DIM);
    for (std::size_t i = 0; i < N * HIGH_DIM; ++i) pts[i] = u(rng);

    std::vector<double> emb1(N * TARGET), emb2(N * TARGET);
    ASSERT_EQ(0, laplacian_eigenmaps(pts.data(), N, HIGH_DIM, K, TARGET, emb1.data()));
    ASSERT_EQ(0, laplacian_eigenmaps(pts.data(), N, HIGH_DIM, K, TARGET, emb2.data()));

    /* Lanczos can flip eigenvector signs across runs; compare absolute
     * values column-wise. The columns themselves should match exactly
     * since the eigendecomposition is deterministic for a deterministic
     * eigensolver on the same input. */
    for (std::size_t i = 0; i < N * TARGET; ++i) {
        EXPECT_NEAR(std::abs(emb1[i]), std::abs(emb2[i]), 1e-12)
            << "non-determinism at element " << i;
    }
}
