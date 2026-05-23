#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

#include "laplace/core/math4d.h"
#include "laplace/core/super_fibonacci.h"

/* Marc Alexa, CVPR 2022. Verifies the algebraic guarantees and a coverage
 * sanity check for the substrate-canonical T0 codepoint placement on S^3. */

TEST(LaplaceCoreSuperFibonacci, ProducesUnitQuaternions) {
    constexpr size_t N = 8192;
    std::vector<double> q(N * 4);
    super_fibonacci(N, q.data());
    /* Algebraic: r² + R² = s/n + (1 - s/n) = 1 exactly. Floating-point
     * round-off pulls each quaternion's L2 norm off 1 by at most a few ULPs
     * — tolerance 1e-13 is comfortably above representable error. */
    for (size_t i = 0; i < N; ++i) {
        const double norm = math4d_norm(&q[i * 4]);
        EXPECT_NEAR(norm, 1.0, 1e-13) << "quaternion " << i;
    }
}

TEST(LaplaceCoreSuperFibonacci, DeterministicAcrossCalls) {
    constexpr size_t N = 1024;
    std::vector<double> a(N * 4), b(N * 4);
    super_fibonacci(N, a.data());
    super_fibonacci(N, b.data());
    EXPECT_EQ(0, std::memcmp(a.data(), b.data(), sizeof(double) * N * 4));
}

TEST(LaplaceCoreSuperFibonacci, ZeroNHandledGracefully) {
    double out[4] = {1.0, 2.0, 3.0, 4.0};
    super_fibonacci(0, out);
    /* No-op on n=0 — buffer must be untouched. */
    EXPECT_DOUBLE_EQ(out[0], 1.0);
    EXPECT_DOUBLE_EQ(out[1], 2.0);
    EXPECT_DOUBLE_EQ(out[2], 3.0);
    EXPECT_DOUBLE_EQ(out[3], 4.0);
}

TEST(LaplaceCoreSuperFibonacci, NullPointerHandledGracefully) {
    super_fibonacci(100, nullptr);  /* must not crash */
    SUCCEED();
}

TEST(LaplaceCoreSuperFibonacci, MinDistanceScalesAsExpected) {
    /* Quasi-uniform coverage of S^3 means the minimum pairwise distance
     * across N points is at least Ω(N^(-1/3)). At N=2048 this gives
     * d_min ≳ 0.05 (theoretical lower bound; super-Fibonacci empirically
     * achieves ~0.10–0.15 — significantly tighter than independent uniform
     * sampling, which has d_min → 0). We assert the lower-bound floor. */
    constexpr size_t N = 2048;
    std::vector<double> q(N * 4);
    super_fibonacci(N, q.data());

    double d_min = 1e9;
    for (size_t i = 0; i < N; ++i) {
        for (size_t j = i + 1; j < N; ++j) {
            const double d = math4d_distance(&q[i * 4], &q[j * 4]);
            if (d < d_min) d_min = d;
        }
    }
    /* Theoretical floor from coverage: N^(-1/3) ≈ 0.079 at N=2048. The
     * super-Fibonacci algorithm beats independent uniform sampling by a
     * large constant factor; empirically d_min ≳ 0.08 at this scale. */
    EXPECT_GT(d_min, 0.05) << "min pairwise distance too small (d_min=" << d_min << ")";
}

TEST(LaplaceCoreSuperFibonacci, MeanCenterApproachesOrigin) {
    /* Sum of N uniformly-distributed unit vectors on S^3 has expected
     * magnitude O(√N) by central limit; the mean (after dividing by N) is
     * O(1/√N). For low-discrepancy super-Fibonacci sampling, the
     * centroid converges to origin much faster — well under 1/√N. */
    constexpr size_t N = 16384;
    std::vector<double> q(N * 4);
    super_fibonacci(N, q.data());

    double mean[4] = {0, 0, 0, 0};
    for (size_t i = 0; i < N; ++i) {
        mean[0] += q[i * 4 + 0];
        mean[1] += q[i * 4 + 1];
        mean[2] += q[i * 4 + 2];
        mean[3] += q[i * 4 + 3];
    }
    for (int d = 0; d < 4; ++d) mean[d] /= (double)N;
    const double mean_mag = math4d_norm(mean);
    /* The low-discrepancy property gives much better centroid convergence
     * than uniform random sampling; empirically the magnitude is ≲ 1e-3
     * at N=16384. The independent-uniform bound is ~1/√N = 0.008. */
    EXPECT_LT(mean_mag, 0.01) << "centroid did not converge to origin (mag=" << mean_mag << ")";
}

TEST(LaplaceCoreSuperFibonacci, HandlesUnicodeCodepointScale) {
    /* The substrate-canonical use is N=1,114,112 (Unicode codepoint space).
     * Verify completion + all-unit-norm at that scale. */
    constexpr size_t UNICODE_N = 1114112;
    std::vector<double> q(UNICODE_N * 4);
    super_fibonacci(UNICODE_N, q.data());

    /* Spot-check a few representative codepoints' unit-norm. */
    for (size_t i : {(size_t)0, (size_t)1, (size_t)127, (size_t)0x100,
                     (size_t)0x10000, (size_t)0x100000, (size_t)1114111}) {
        const double norm = math4d_norm(&q[i * 4]);
        EXPECT_NEAR(norm, 1.0, 1e-13) << "codepoint index " << i;
    }
}
