#include <gtest/gtest.h>

#include <cmath>
#include <limits>
#include <vector>

#include "laplace/synthesis/tensor_decompose.h"

static std::vector<float> MakeRank2() {
    std::vector<float> A(4 * 3, 0.0f);
    A[0 * 3 + 0] = 10.0f;
    A[1 * 3 + 1] = 0.01f;
    return A;
}

static double FrobError(const std::vector<float>& A, size_t m, size_t n,
                        const std::vector<float>& U, const std::vector<float>& S,
                        const std::vector<float>& Vt, size_t r, size_t kmax) {
    double err = 0.0;
    for (size_t i = 0; i < m; ++i)
        for (size_t j = 0; j < n; ++j) {
            double rec = 0.0;
            for (size_t t = 0; t < r; ++t)
                rec += (double)U[i * kmax + t] * (double)S[t] * (double)Vt[t * n + j];
            const double d = (double)A[i * n + j] - rec;
            err += d * d;
        }
    return std::sqrt(err);
}

TEST(TensorDecompose, NullArgsRejected) {
    float buf[16]; size_t r = 99;
    EXPECT_EQ(tensor_svd_truncate(nullptr, 4, 3, 0.0, &r, buf, buf, buf, 3), -1);
    auto A = MakeRank2();
    EXPECT_EQ(tensor_svd_truncate(A.data(), 4, 3, 0.0, nullptr, buf, buf, buf, 3), -1);
    EXPECT_EQ(tensor_svd_truncate(A.data(), 0, 3, 0.0, &r, buf, buf, buf, 3), -1);
    EXPECT_EQ(tensor_svd_truncate(A.data(), 4, 3, 1.0, &r, buf, buf, buf, 3), -1);
    EXPECT_EQ(tensor_svd_truncate(A.data(), 4, 3, 0.0, &r, buf, buf, buf, 0), -1)
        << "kmax must provide at least one output slot";
    EXPECT_EQ(tensor_svd_truncate(A.data(), 4, 3,
        std::numeric_limits<double>::quiet_NaN(), &r, buf, buf, buf, 3), -1);
}

TEST(TensorDecompose, FullRankWhenTolZero) {
    auto A = MakeRank2();
    const size_t m = 4, n = 3, kmax = 3;
    std::vector<float> U(m * kmax), S(kmax), Vt(kmax * n);
    size_t r = 0;
    int rc = tensor_svd_truncate(A.data(), m, n, 0.0, &r, U.data(), S.data(), Vt.data(), kmax);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, 0);
    EXPECT_EQ(r, 2u) << "both nonzero modes retained at tol=0";
    EXPECT_NEAR(S[0], 10.0f, 1e-3);
    EXPECT_NEAR(S[1], 0.01f, 1e-4);
    EXPECT_LT(FrobError(A, m, n, U, S, Vt, r, kmax), 1e-3);
}

TEST(TensorDecompose, AdaptiveRankDropsNegligibleMode) {
    auto A = MakeRank2();
    const size_t m = 4, n = 3, kmax = 3;
    std::vector<float> U(m * kmax), S(kmax), Vt(kmax * n);
    size_t r = 0;
    int rc = tensor_svd_truncate(A.data(), m, n, 0.1, &r, U.data(), S.data(), Vt.data(), kmax);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, 0);
    EXPECT_EQ(r, 1u) << "negligible second mode dropped within tolerance";
    const double normA = std::sqrt(10.0 * 10.0 + 0.01 * 0.01);
    EXPECT_LE(FrobError(A, m, n, U, S, Vt, r, kmax), 0.1 * normA + 1e-5);
}

TEST(TensorDecompose, AllZeroTensorHasRankZero) {
    std::vector<float> A(4 * 3, 0.0f);
    const size_t m = 4, n = 3, kmax = 3;
    std::vector<float> U(m * kmax), S(kmax), Vt(kmax * n);
    size_t r = 99;
    int rc = tensor_svd_truncate(A.data(), m, n, 0.0, &r, U.data(), S.data(), Vt.data(), kmax);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, 0);
    EXPECT_EQ(r, 0u);
}

TEST(TensorDecompose, InsufficientCapacityDoesNotOverrideZeroLossBudget) {
    const size_t m = 100, n = 8, kmax = 2;
    std::vector<float> A(m * n, 0.0f);
    for (size_t i = 0; i < m; ++i) {
        A[i * n + 0] = 10.0f * ((i % 2) ? 1.0f : -1.0f);
        A[i * n + 1] = 3.0f  * ((i % 3) ? 1.0f : 0.0f);
        A[i * n + 2] = 1.0f  * ((i % 5) ? 1.0f : 0.0f);
        A[i * n + 3] = 0.3f  * ((i % 7) ? 1.0f : 0.0f);
    }
    std::vector<float> U(m * kmax, -99), S(kmax, -99), Vt(kmax * n, -99);
    size_t r = 99;
    int rc = tensor_svd_truncate(A.data(), m, n, 0.0, &r, U.data(), S.data(), Vt.data(), kmax);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, -3);
    EXPECT_GT(r, kmax);
    for (float v : U) EXPECT_EQ(v, -99);
    for (float v : S) EXPECT_EQ(v, -99);
    for (float v : Vt) EXPECT_EQ(v, -99);
}

TEST(TensorDecompose, CapacityBelowMinDimensionWorksWhenBudgetPermits) {
    auto A = MakeRank2();
    const size_t m = 4, n = 3, kmax = 1;
    std::vector<float> U(m * kmax), S(kmax), Vt(kmax * n);
    size_t r = 99;
    const int rc = tensor_svd_truncate(A.data(), m, n, 0.1, &r,
        U.data(), S.data(), Vt.data(), kmax);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, 0);
    EXPECT_EQ(r, 1u);
    EXPECT_LE(FrobError(A, m, n, U, S, Vt, r, kmax),
        0.1 * std::sqrt(100.0 + 0.0001) + 1e-5);
}

TEST(TensorDecompose, InsufficientCapacityDoesNotOverrideNonzeroLossBudget) {
    auto A = MakeRank2();
    std::vector<float> U(4, -99), S(1, -99), Vt(3, -99);
    size_t r = 99;
    const int rc = tensor_svd_truncate(A.data(), 4, 3, 0.0001, &r,
        U.data(), S.data(), Vt.data(), 1);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, -3);
    EXPECT_EQ(r, 2u);
    for (float v : U) EXPECT_EQ(v, -99);
    for (float v : S) EXPECT_EQ(v, -99);
    for (float v : Vt) EXPECT_EQ(v, -99);
}

TEST(TensorDecompose, NonfiniteTensorRejectedWithoutWritingFactors) {
    auto A = MakeRank2();
    A[2] = std::numeric_limits<float>::infinity();
    std::vector<float> U(12, -99), S(3, -99), Vt(9, -99);
    size_t r = 99;
    const int rc = tensor_svd_truncate(A.data(), 4, 3, 0.0, &r,
        U.data(), S.data(), Vt.data(), 3);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, -1);
    for (float v : U) EXPECT_EQ(v, -99);
    for (float v : S) EXPECT_EQ(v, -99);
    for (float v : Vt) EXPECT_EQ(v, -99);
}
