#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <numeric>
#include <random>
#include <vector>

#include "laplace/dynamics/sparsity.h"

/* === Multi-pass stub remains -1 until Chunk 6 lands === */

TEST(LaplaceDynamicsSparsity, StubPerTensorReturnsError) {
    double weights[10] = {1,2,3,4,5,6,7,8,9,10};
    uint8_t mask[10] = {};
    sparsity_params_t params = {0.5, 2};
    int rc = sparsity_per_tensor_topk(weights, 10, &params, mask);
    EXPECT_NE(rc, 0);
}

/* === Streaming variants (Framework Epic #232 / Stories B.1 + B.2) === */

TEST(LaplaceDynamicsSparsityStreaming, PerTensorRejectsInvalidArgs) {
    double v[3] = {1, 2, 3};
    uint8_t mask[3] = {0};
    EXPECT_NE(0, sparsity_per_tensor_topk_streaming(nullptr, 3, 0.5, mask));
    EXPECT_NE(0, sparsity_per_tensor_topk_streaming(v, 3, 0.5, nullptr));
    EXPECT_NE(0, sparsity_per_tensor_topk_streaming(v, 0, 0.5, mask));
    EXPECT_NE(0, sparsity_per_tensor_topk_streaming(v, 3, 0.0, mask));
    EXPECT_NE(0, sparsity_per_tensor_topk_streaming(v, 3, -0.1, mask));
    EXPECT_NE(0, sparsity_per_tensor_topk_streaming(v, 3, 1.1, mask));
}

TEST(LaplaceDynamicsSparsityStreaming, PerTensorTop100PctRetainsAll) {
    double v[5] = {-3, 1, -4, 1, 5};
    uint8_t mask[5] = {0};
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v, 5, 1.0, mask));
    for (int i = 0; i < 5; ++i) EXPECT_EQ(1, mask[i]) << "i=" << i;
}

TEST(LaplaceDynamicsSparsityStreaming, PerTensorTop50PctRetainsTopHalfByAbs) {
    /* |v| = {3, 1, 4, 1, 5}; sorted desc = {5, 4, 3, 1, 1}; k = ceil(5*0.5)=3
     * threshold = 3rd largest = 3 → mask indices where |v| >= 3 → 0,2,4 retained */
    double v[5] = {-3, 1, -4, 1, 5};
    uint8_t mask[5] = {0};
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v, 5, 0.5, mask));
    EXPECT_EQ(1, mask[0]);  /* |-3|=3 */
    EXPECT_EQ(0, mask[1]);  /* |1|=1 */
    EXPECT_EQ(1, mask[2]);  /* |-4|=4 */
    EXPECT_EQ(0, mask[3]);  /* |1|=1 */
    EXPECT_EQ(1, mask[4]);  /* |5|=5 */
}

TEST(LaplaceDynamicsSparsityStreaming, PerTensorVerySmallPctRetainsAtLeastOne) {
    /* topk_pct = 1e-6 with n=10 → k_d = ceil(1e-5) = 1; mandatory min 1. */
    double v[10] = {1,2,3,4,5,6,7,8,9,10};
    uint8_t mask[10] = {0};
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v, 10, 1e-6, mask));
    int retained = 0;
    for (int i = 0; i < 10; ++i) retained += mask[i];
    EXPECT_EQ(1, retained);
    EXPECT_EQ(1, mask[9]);  /* the largest */
}

TEST(LaplaceDynamicsSparsityStreaming, PerTensorTiesAtThresholdAllRetained) {
    /* |v| = {1,1,1,1,1} all equal; any k% → threshold = 1 → all retained */
    double v[5] = {1, -1, 1, -1, 1};
    uint8_t mask[5] = {0};
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v, 5, 0.2, mask));
    for (int i = 0; i < 5; ++i) EXPECT_EQ(1, mask[i]);
}

TEST(LaplaceDynamicsSparsityStreaming, PerTensorDeterministicAcrossRuns) {
    /* Determinism (RULES R7): same input → byte-identical mask on every run. */
    std::mt19937_64 rng(0xCAFEBABEull);
    std::uniform_real_distribution<double> dist(-1.0, 1.0);
    const size_t N = 100'000;
    std::vector<double> v(N);
    for (auto& x : v) x = dist(rng);

    std::vector<uint8_t> mask_a(N, 0), mask_b(N, 0);
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v.data(), N, 0.1, mask_a.data()));
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v.data(), N, 0.1, mask_b.data()));
    EXPECT_EQ(0, std::memcmp(mask_a.data(), mask_b.data(), N));
}

TEST(LaplaceDynamicsSparsityStreaming, PerTensorRetainCountWithinTolerance) {
    /* Verify retained count is approximately ceil(n * topk_pct). Ties may
     * push slightly above; should never be below. */
    std::mt19937_64 rng(0xDEADBEEFull);
    std::uniform_real_distribution<double> dist(-10.0, 10.0);
    const size_t N = 10'000;
    const double pct = 0.05;
    std::vector<double> v(N);
    for (auto& x : v) x = dist(rng);
    std::vector<uint8_t> mask(N, 0);
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v.data(), N, pct, mask.data()));

    const size_t expected_k = (size_t)std::ceil((double)N * pct);
    size_t retained = 0;
    for (size_t i = 0; i < N; ++i) retained += mask[i];
    EXPECT_GE(retained, expected_k);
    /* Slack for ties: random uniform → essentially no ties; retained ≈ k */
    EXPECT_LE(retained, expected_k + 2);
}

/* === Per-row top-k === */

TEST(LaplaceDynamicsSparsityStreaming, PerRowRejectsInvalidArgs) {
    double rows[6] = {1,2,3,4,5,6};
    uint8_t mask[6] = {0};
    EXPECT_NE(0, sparsity_per_row_topk_streaming(nullptr, 2, 3, 1, mask));
    EXPECT_NE(0, sparsity_per_row_topk_streaming(rows, 2, 3, 1, nullptr));
    EXPECT_NE(0, sparsity_per_row_topk_streaming(rows, 0, 3, 1, mask));
    EXPECT_NE(0, sparsity_per_row_topk_streaming(rows, 2, 0, 1, mask));
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowKZeroPrunesEntirely) {
    double rows[6] = {1,2,3,4,5,6};
    uint8_t mask[6] = {1,1,1,1,1,1};
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows, 2, 3, 0, mask));
    for (int i = 0; i < 6; ++i) EXPECT_EQ(0, mask[i]);
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowKGEQRowSizeRetainsAll) {
    double rows[6] = {1,2,3,4,5,6};
    uint8_t mask[6] = {0};
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows, 2, 3, 3, mask));
    for (int i = 0; i < 6; ++i) EXPECT_EQ(1, mask[i]);
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows, 2, 3, 10, mask));
    for (int i = 0; i < 6; ++i) EXPECT_EQ(1, mask[i]);
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowTop2Of4) {
    /* row 0: |{-1, 4, -2, 3}| -> top-2 by abs = {4, 3} -> mask {0,1,0,1}
     * row 1: |{5, -1, 0, 2}|  -> top-2 by abs = {5, 2} -> mask {1,0,0,1} */
    double rows[8] = {
        -1, 4, -2, 3,
         5,-1,  0, 2,
    };
    uint8_t mask[8] = {0};
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows, 2, 4, 2, mask));
    EXPECT_EQ(0, mask[0]);
    EXPECT_EQ(1, mask[1]);
    EXPECT_EQ(0, mask[2]);
    EXPECT_EQ(1, mask[3]);
    EXPECT_EQ(1, mask[4]);
    EXPECT_EQ(0, mask[5]);
    EXPECT_EQ(0, mask[6]);
    EXPECT_EQ(1, mask[7]);
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowTiesAtThresholdAllRetained) {
    double rows[4] = {1, 1, 1, 1};
    uint8_t mask[4] = {0};
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows, 1, 4, 2, mask));
    /* All four equal at |1|; threshold=1; all retained — exceeds k=2, OK. */
    for (int i = 0; i < 4; ++i) EXPECT_EQ(1, mask[i]);
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowDeterministicAcrossRuns) {
    std::mt19937_64 rng(0xFEEDFACEull);
    std::uniform_real_distribution<double> dist(-1.0, 1.0);
    const size_t R = 1000, C = 256;
    std::vector<double> rows(R * C);
    for (auto& x : rows) x = dist(rng);
    std::vector<uint8_t> mask_a(R * C, 0), mask_b(R * C, 0);
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows.data(), R, C, 8, mask_a.data()));
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows.data(), R, C, 8, mask_b.data()));
    EXPECT_EQ(0, std::memcmp(mask_a.data(), mask_b.data(), R * C));
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowEachRowExactlyKRetainedNoTies) {
    /* Distinct values per row → exactly k retained (no ties). */
    const size_t R = 500, C = 64, K = 4;
    std::vector<double> rows(R * C);
    for (size_t r = 0; r < R; ++r) {
        for (size_t c = 0; c < C; ++c) {
            /* unique values per (r,c) — magnitude depends on c so largest
             * c gets largest abs */
            rows[r * C + c] = (double)c + (double)r * 1e-6;
        }
    }
    std::vector<uint8_t> mask(R * C, 0);
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows.data(), R, C, K, mask.data()));
    for (size_t r = 0; r < R; ++r) {
        size_t retained = 0;
        for (size_t c = 0; c < C; ++c) retained += mask[r * C + c];
        EXPECT_EQ(K, retained) << "row " << r;
        /* The top-K columns are the largest C values: C-K..C-1 */
        for (size_t c = 0; c < C - K; ++c) EXPECT_EQ(0, mask[r * C + c]);
        for (size_t c = C - K; c < C; ++c) EXPECT_EQ(1, mask[r * C + c]);
    }
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowScalesTo10kRowsX1kCols) {
    /* Soak: 10k rows × 1k cols, k=10. Correctness only — kernel-time perf
     * gate is asserted separately below to isolate from data setup. */
    const size_t R = 10'000, C = 1'000, K = 10;
    std::vector<double> rows(R * C);
    std::mt19937_64 rng(0xBADD'C0DE);
    std::uniform_real_distribution<double> dist(-1.0, 1.0);
    for (auto& x : rows) x = dist(rng);
    std::vector<uint8_t> mask(R * C, 0);
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows.data(), R, C, K, mask.data()));
    size_t row0 = 0;
    for (size_t c = 0; c < C; ++c) row0 += mask[c];
    EXPECT_GE(row0, K);
    EXPECT_LE(row0, K + 2);
}

TEST(LaplaceDynamicsSparsityStreaming, PerRowPerfGate10kX1kKernelTime) {
    /* Story B.2 acceptance gate: 10⁴ rows × 10³ cols, k=10 → ≤ 50 ms on AVX2
     * dev box. Measures ONLY the kernel time (data setup excluded). Warm-up
     * pass to JIT/cache-prime; then median of 3 trials. */
    const size_t R = 10'000, C = 1'000, K = 10;
    std::vector<double> rows(R * C);
    std::mt19937_64 rng(0xBADD'C0DE);
    std::uniform_real_distribution<double> dist(-1.0, 1.0);
    for (auto& x : rows) x = dist(rng);
    std::vector<uint8_t> mask(R * C, 0);

    /* Warm-up */
    ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows.data(), R, C, K, mask.data()));

    /* Measured trials */
    std::vector<double> ms_trials;
    for (int t = 0; t < 3; ++t) {
        const auto t0 = std::chrono::steady_clock::now();
        ASSERT_EQ(0, sparsity_per_row_topk_streaming(rows.data(), R, C, K, mask.data()));
        const auto t1 = std::chrono::steady_clock::now();
        ms_trials.push_back(std::chrono::duration<double, std::milli>(t1 - t0).count());
    }
    std::sort(ms_trials.begin(), ms_trials.end());
    const double median_ms = ms_trials[1];
    std::cerr << "[PerRow 10k×1k k=10] trials ms = "
              << ms_trials[0] << ", " << ms_trials[1] << ", " << ms_trials[2]
              << " (median=" << median_ms << ")\n";
    EXPECT_LE(median_ms, 50.0)
        << "Story B.2 perf gate exceeded: kernel-time median " << median_ms
        << " ms > 50 ms target on this dev box";
}

TEST(LaplaceDynamicsSparsityStreaming, PerTensorPerfGate100kKernelTime) {
    /* No documented hard gate, but record cost so regressions surface.
     * 100K elements, top-10% via streaming. */
    std::mt19937_64 rng(0xBADD'F00D);
    std::uniform_real_distribution<double> dist(-1.0, 1.0);
    const size_t N = 100'000;
    std::vector<double> v(N);
    for (auto& x : v) x = dist(rng);
    std::vector<uint8_t> mask(N, 0);
    ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v.data(), N, 0.1, mask.data()));

    std::vector<double> ms_trials;
    for (int t = 0; t < 3; ++t) {
        const auto t0 = std::chrono::steady_clock::now();
        ASSERT_EQ(0, sparsity_per_tensor_topk_streaming(v.data(), N, 0.1, mask.data()));
        const auto t1 = std::chrono::steady_clock::now();
        ms_trials.push_back(std::chrono::duration<double, std::milli>(t1 - t0).count());
    }
    std::sort(ms_trials.begin(), ms_trials.end());
    std::cerr << "[PerTensor 100k topk=10%] trials ms = "
              << ms_trials[0] << ", " << ms_trials[1] << ", " << ms_trials[2] << "\n";
    /* Soft upper bound: 100k doubles + nth_element + masking pass should
     * be well under 50 ms even single-threaded. Surface regressions. */
    EXPECT_LE(ms_trials[1], 50.0);
}
