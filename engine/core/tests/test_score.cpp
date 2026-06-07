#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <vector>

#include "laplace/core/score.h"

namespace {

::testing::AssertionResult close_rel(double actual, double expected, double rel) {
    double diff = std::fabs(actual - expected);
    double tol  = std::max(rel, rel * std::fabs(expected));
    if (diff <= tol) return ::testing::AssertionSuccess();
    return ::testing::AssertionFailure()
        << "actual=" << actual << " expected=" << expected
        << " diff=" << diff << " tol=" << tol;
}

}

TEST(LaplaceCoreScore, ZeroIsExactMidpoint) {
    EXPECT_EQ(laplace_score_fp(0.0, 0.02), 500000000LL);
    EXPECT_EQ(laplace_score_fp(0.0, 1.0), 500000000LL);
}

TEST(LaplaceCoreScore, RangeIsExclusiveOfEndpoints) {
    const double ms[] = {0.02, 1.0};
    const double vs[] = {0.001, 0.1, 1.0, 2.0, 6.0, 15.0, 100.0, 1000.0};
    for (double m : ms) {
        for (double v : vs) {
            int64_t sp = laplace_score_fp(v, m);
            int64_t sn = laplace_score_fp(-v, m);
            EXPECT_GT(sp, 0LL);
            EXPECT_LT(sp, 1000000000LL);
            EXPECT_GT(sn, 0LL);
            EXPECT_LT(sn, 1000000000LL);
        }
    }
}

TEST(LaplaceCoreScore, RoundTripRelative) {
    const double ms[] = {0.02, 1.0};
    const double vs[] = {0.001, 0.1, 1.0, 2.0, 6.0, 15.0, 100.0, 1000.0};
    for (double m : ms) {
        for (double v : vs) {
            for (double sign : {1.0, -1.0}) {
                double vv = sign * v;
                int64_t sp = laplace_score_fp(vv, m);
                double vr = laplace_score_inverse_fp(sp, m);

                EXPECT_EQ(laplace_score_fp(vr, m), sp)
                    << "m=" << m << " v=" << vv;

                double a = std::fabs(vv);
                double dvm_ds = (m + a) * (m + a) / (m * m) * 4.0e-9;
                double tol = std::max(1e-6 * std::fabs(vv / m), dvm_ds);
                EXPECT_LE(std::fabs(vr / m - vv / m), tol)
                    << "m=" << m << " v=" << vv
                    << " vr=" << vr << " tol=" << tol;
            }
        }
    }
}

TEST(LaplaceCoreScore, MonotonicSweep) {
    const double m = 1.0;
    int64_t prev = laplace_score_fp(-1000.0, m);
    for (double v = -999.0; v <= 1000.0; v += 1.0) {
        int64_t cur = laplace_score_fp(v, m);
        EXPECT_GE(cur, prev) << "v=" << v;
        prev = cur;
    }
}

TEST(LaplaceCoreScore, DistinguishesLargeTailValues) {
    const double m = 1.0;
    int64_t s15  = laplace_score_fp(15.0 * m, m);
    int64_t s100 = laplace_score_fp(100.0 * m, m);
    EXPECT_NE(s15, s100);

    double v15  = laplace_score_inverse_fp(s15, m);
    double v100 = laplace_score_inverse_fp(s100, m);
    EXPECT_TRUE(close_rel(v15, 15.0 * m, 1e-4));
    EXPECT_TRUE(close_rel(v100, 100.0 * m, 1e-4));
    EXPECT_GT(std::fabs(v100 - v15), 0.0);
}

TEST(LaplaceCoreScore, EndpointsClampWithoutDivByZero) {
    const double m = 1.0;
    double lo = laplace_score_inverse_fp(0LL, m);
    double hi = laplace_score_inverse_fp(1000000000LL, m);
    EXPECT_TRUE(std::isfinite(lo));
    EXPECT_TRUE(std::isfinite(hi));
    EXPECT_LT(lo, 0.0);
    EXPECT_GT(hi, 0.0);
}

TEST(LaplaceCoreScore, BatchMatchesScalar) {
    const double m = 0.5;
    std::vector<float> w = {-1000.0f, -15.0f, -1.0f, 0.0f, 1.0f, 15.0f, 1000.0f};
    std::vector<int64_t> out(w.size());
    laplace_score_batch_fp(w.data(), w.size(), m, out.data());
    for (size_t i = 0; i < w.size(); ++i) {
        EXPECT_EQ(out[i], laplace_score_fp((double)w[i], m)) << "i=" << i;
    }
}
