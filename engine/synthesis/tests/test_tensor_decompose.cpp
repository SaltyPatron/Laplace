#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/synthesis/tensor_decompose.h"

/* A = 10 * e0 e0^T  +  0.01 * e1 e1^T   (m=4, n=3, exact rank 2)
 * Singular values {10, 0.01}; orthonormal singular vectors. Lets us assert the
 * adaptive rank and the Eckart-Young error bound exactly. */
static std::vector<float> MakeRank2() {
    std::vector<float> A(4 * 3, 0.0f);
    A[0 * 3 + 0] = 10.0f;   /* row 0, col 0 */
    A[1 * 3 + 1] = 0.01f;   /* row 1, col 1 */
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
    EXPECT_EQ(tensor_svd_truncate(A.data(), 4, 3, 1.0, &r, buf, buf, buf, 3), -1); /* tol out of range */
    EXPECT_EQ(tensor_svd_truncate(A.data(), 4, 3, 0.0, &r, buf, buf, buf, 1), -1); /* kmax < min(m,n) */
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
    /* tol=0.1: budget = 0.01*total ~ 1.0; the 0.01-mode (energy 1e-4) is droppable. */
    int rc = tensor_svd_truncate(A.data(), m, n, 0.1, &r, U.data(), S.data(), Vt.data(), kmax);
    if (rc == -2) GTEST_SKIP() << "LAPACK/MKL unavailable in this build";
    ASSERT_EQ(rc, 0);
    EXPECT_EQ(r, 1u) << "negligible second mode dropped within tolerance";
    /* Eckart-Young: actual error must respect the requested relative bound. */
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
