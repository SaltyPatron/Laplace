#include <gtest/gtest.h>

#include <cmath>
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
    EXPECT_EQ(tensor_svd_truncate(A.data(), 4, 3, 0.0, &r, buf, buf, buf, 0), -1)  // only kmax==0 is invalid
        << "kmax is an output-rank cap; only 0 is rejected (kmax<min(m,n) is the normal truncation case)";
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

// Regression for the SIMILAR_TO ingest failure: a TALL matrix reduced to a target rank far below
// min(m,n) — exactly a 32000×2048 model embedding asking for rank 64 (scaled here to 100×8 → rank 2).
// The old `kmax < min(m,n) → -1` guard rejected this, silently disabling the whole plane.
TEST(TensorDecompose, TruncatesTallMatrixToKmaxBelowMinDim) {
    const size_t m = 100, n = 8, kmax = 2;
    std::vector<float> A(m * n, 0.0f);
    // Four nonzero columns of clearly decreasing energy → true rank 4 > kmax, forcing truncation.
    for (size_t i = 0; i < m; ++i) {
        A[i * n + 0] = 10.0f * ((i % 2) ? 1.0f : -1.0f);
        A[i * n + 1] = 3.0f  * ((i % 3) ? 1.0f : 0.0f);
        A[i * n + 2] = 1.0f  * ((i % 5) ? 1.0f : 0.0f);
        A[i * n + 3] = 0.3f  * ((i % 7) ? 1.0f : 0.0f);
    }
    std::vector<float> U(m * kmax), S(kmax), Vt(kmax * n);
    size_t r = 99;
    int rc = tensor_svd_truncate(A.data(), m, n, 0.0, &r, U.data(), S.data(), Vt.data(), kmax);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, 0) << "tall-matrix truncation to kmax < min(m,n) must succeed (was the SVD bug)";
    EXPECT_LE(r, kmax) << "kept rank must honor the output cap";
    EXPECT_GE(r, 1u);
    EXPECT_GT(S[0], 0.0f);
    EXPECT_GE(S[0], S[r - 1]) << "singular values descending";
}
