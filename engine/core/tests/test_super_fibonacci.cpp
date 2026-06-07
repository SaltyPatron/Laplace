#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

#include "laplace/core/math4d.h"
#include "laplace/core/super_fibonacci.h"

TEST(LaplaceCoreSuperFibonacci, ProducesUnitQuaternions) {
    constexpr size_t N = 8192;
    std::vector<double> q(N * 4);
    super_fibonacci(N, q.data());
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
    EXPECT_DOUBLE_EQ(out[0], 1.0);
    EXPECT_DOUBLE_EQ(out[1], 2.0);
    EXPECT_DOUBLE_EQ(out[2], 3.0);
    EXPECT_DOUBLE_EQ(out[3], 4.0);
}

TEST(LaplaceCoreSuperFibonacci, NullPointerHandledGracefully) {
    super_fibonacci(100, nullptr);
    SUCCEED();
}

TEST(LaplaceCoreSuperFibonacci, MinDistanceScalesAsExpected) {
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
    EXPECT_GT(d_min, 0.05) << "min pairwise distance too small (d_min=" << d_min << ")";
}

TEST(LaplaceCoreSuperFibonacci, MeanCenterApproachesOrigin) {
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
    EXPECT_LT(mean_mag, 0.01) << "centroid did not converge to origin (mag=" << mean_mag << ")";
}

namespace {
void hopf_map(const double* q, double out_s2[3]) {
    const double x0 = q[0], x1 = q[1], x2 = q[2], x3 = q[3];
    out_s2[0] = 2.0 * (x0 * x2 + x1 * x3);
    out_s2[1] = 2.0 * (x1 * x2 - x0 * x3);
    out_s2[2] = (x0 * x0 + x1 * x1) - (x2 * x2 + x3 * x3);
}
}

TEST(LaplaceCoreSuperFibonacci, HopfBaseLiesOnS2) {
    constexpr size_t N = 8192;
    std::vector<double> q(N * 4);
    super_fibonacci(N, q.data());
    for (size_t i = 0; i < N; ++i) {
        double b[3];
        hopf_map(&q[i * 4], b);
        const double norm = std::sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
        EXPECT_NEAR(norm, 1.0, 1e-12) << "Hopf base of point " << i << " off S²";
    }
}

TEST(LaplaceCoreSuperFibonacci, HopfPolarHeightEquidistributed) {
    constexpr size_t N = 65536;
    std::vector<double> q(N * 4);
    super_fibonacci(N, q.data());

    constexpr int BINS = 64;
    std::vector<size_t> hist(BINS, 0);
    for (size_t i = 0; i < N; ++i) {
        double b[3];
        hopf_map(&q[i * 4], b);
        const double expected = 2.0 * ((double)i + 0.5) / (double)N - 1.0;
        EXPECT_NEAR(b[2], expected, 1e-12) << "height non-linear at " << i;
        int bin = (int)((b[2] + 1.0) * 0.5 * BINS);
        if (bin < 0) bin = 0;
        if (bin >= BINS) bin = BINS - 1;
        hist[(size_t)bin] += 1;
    }
    const size_t expect_per = N / BINS;
    for (int k = 0; k < BINS; ++k) {
        EXPECT_LE(hist[(size_t)k], expect_per + 1) << "band " << k << " overfull";
        EXPECT_GE(hist[(size_t)k], expect_per - 1) << "band " << k << " underfull";
    }
}

TEST(LaplaceCoreSuperFibonacci, HopfCircleAnglesEquidistributed) {
    constexpr size_t N = 65536;
    std::vector<double> q(N * 4);
    super_fibonacci(N, q.data());

    constexpr int BINS = 32;
    std::vector<size_t> hist_a(BINS, 0), hist_b(BINS, 0);
    const double two_pi = 6.283185307179586;
    for (size_t i = 0; i < N; ++i) {
        const double* p = &q[i * 4];
        double alpha = std::atan2(p[0], p[1]);
        double beta  = std::atan2(p[2], p[3]);
        if (alpha < 0) alpha += two_pi;
        if (beta  < 0) beta  += two_pi;
        int ba = (int)(alpha / two_pi * BINS); if (ba >= BINS) ba = BINS - 1;
        int bb = (int)(beta  / two_pi * BINS); if (bb >= BINS) bb = BINS - 1;
        hist_a[(size_t)ba] += 1;
        hist_b[(size_t)bb] += 1;
    }
    const double mean = (double)N / BINS;
    for (int k = 0; k < BINS; ++k) {
        EXPECT_GT((double)hist_a[(size_t)k], mean * 0.8) << "α band " << k << " sparse";
        EXPECT_LT((double)hist_a[(size_t)k], mean * 1.2) << "α band " << k << " dense";
        EXPECT_GT((double)hist_b[(size_t)k], mean * 0.8) << "β band " << k << " sparse";
        EXPECT_LT((double)hist_b[(size_t)k], mean * 1.2) << "β band " << k << " dense";
    }
}

TEST(LaplaceCoreSuperFibonacci, HandlesUnicodeCodepointScale) {
    constexpr size_t UNICODE_N = 1114112;
    std::vector<double> q(UNICODE_N * 4);
    super_fibonacci(UNICODE_N, q.data());

    for (size_t i : {(size_t)0, (size_t)1, (size_t)127, (size_t)0x100,
                     (size_t)0x10000, (size_t)0x100000, (size_t)1114111}) {
        const double norm = math4d_norm(&q[i * 4]);
        EXPECT_NEAR(norm, 1.0, 1e-13) << "codepoint index " << i;
    }
}
