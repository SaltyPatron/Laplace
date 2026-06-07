#include <gtest/gtest.h>

#include <Eigen/Core>
#include <cmath>
#include <random>
#include <vector>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#include "laplace/dynamics/eigenmaps.h"

namespace {

bool embedding_preserves_order_on_ring(const double* low, std::size_t n,
                                       std::size_t target_dim) {
    if (target_dim < 2) return false;
    std::vector<std::pair<double, std::size_t>> angles(n);
    for (std::size_t i = 0; i < n; ++i) {
        angles[i] = {std::atan2(low[i * target_dim + 1], low[i * target_dim + 0]), i};
    }
    std::sort(angles.begin(), angles.end());

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

}

TEST(LaplaceDynamicsEigenmaps, RejectsNullInputs) {
    double out[16];
    EXPECT_EQ(-1, laplacian_eigenmaps(nullptr, 4, 3, 2, 4, out));
    double pts[12] = {0};
    EXPECT_EQ(-1, laplacian_eigenmaps(pts, 4, 3, 2, 4, nullptr));
}

TEST(LaplaceDynamicsEigenmaps, RejectsInvalidArgs) {
    double pts[12] = {0};
    double out[16];
    EXPECT_EQ(-2, laplacian_eigenmaps(pts, 4, 3, 5, 2, out));
    EXPECT_EQ(-2, laplacian_eigenmaps(pts, 4, 3, 2, 4, out));
    EXPECT_EQ(-2, laplacian_eigenmaps(pts, 0, 3, 2, 4, out));
}

TEST(LaplaceDynamicsEigenmaps, RecoversRingManifold) {
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

    for (std::size_t i = 0; i < N * TARGET; ++i) {
        EXPECT_NEAR(std::abs(emb1[i]), std::abs(emb2[i]), 1e-12)
            << "non-determinism at element " << i;
    }
}
