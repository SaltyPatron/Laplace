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

TEST(LaplaceDynamicsEigenmaps, EmbeddingHasZeroMeanPerDim) {
    /* Laplacian eigenvectors are orthogonal to the constant eigenvector,
     * so each output coordinate column should have mean ≈ 0. */
    constexpr std::size_t N = 80;
    constexpr std::size_t HIGH_DIM = 5;
    constexpr std::size_t K = 5;
    constexpr std::size_t TARGET = 3;

    std::mt19937_64 rng(0x123ULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);
    std::vector<double> pts(N * HIGH_DIM);
    for (std::size_t i = 0; i < N * HIGH_DIM; ++i) pts[i] = u(rng);

    std::vector<double> emb(N * TARGET);
    ASSERT_EQ(0, laplacian_eigenmaps(pts.data(), N, HIGH_DIM, K, TARGET, emb.data()));

    for (std::size_t k = 0; k < TARGET; ++k) {
        double mean = 0.0;
        for (std::size_t i = 0; i < N; ++i) mean += emb[i * TARGET + k];
        mean /= static_cast<double>(N);
        EXPECT_NEAR(mean, 0.0, 1e-9)
            << "embedding dim " << k << " not zero-mean (mean=" << mean << ")";
    }
}

TEST(LaplaceDynamicsEigenmaps, EmbeddingColumnsAreOrthonormal) {
    /* Spectra returns unit-normalized eigenvectors; the embedding
     * columns inherit this orthonormality. */
    constexpr std::size_t N = 100;
    constexpr std::size_t HIGH_DIM = 8;
    constexpr std::size_t K = 6;
    constexpr std::size_t TARGET = 4;

    std::mt19937_64 rng(0xDEADBEEFULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);
    std::vector<double> pts(N * HIGH_DIM);
    for (std::size_t i = 0; i < N * HIGH_DIM; ++i) pts[i] = u(rng);

    std::vector<double> emb(N * TARGET);
    ASSERT_EQ(0, laplacian_eigenmaps(pts.data(), N, HIGH_DIM, K, TARGET, emb.data()));

    for (std::size_t a = 0; a < TARGET; ++a) {
        double norm_sq_a = 0.0;
        for (std::size_t i = 0; i < N; ++i) {
            const double v = emb[i * TARGET + a];
            norm_sq_a += v * v;
        }
        EXPECT_NEAR(norm_sq_a, 1.0, 1e-9)
            << "embedding column " << a << " norm² = " << norm_sq_a;

        for (std::size_t b = a + 1; b < TARGET; ++b) {
            double dot = 0.0;
            for (std::size_t i = 0; i < N; ++i) {
                dot += emb[i * TARGET + a] * emb[i * TARGET + b];
            }
            EXPECT_NEAR(dot, 0.0, 1e-9)
                << "columns " << a << " and " << b << " not orthogonal (dot=" << dot << ")";
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
