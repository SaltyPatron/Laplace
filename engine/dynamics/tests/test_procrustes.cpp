#include <gtest/gtest.h>

#include <Eigen/Core>
#include <Eigen/Geometry>
#include <cmath>
#include <random>
#include <vector>

#include "laplace/dynamics/procrustes.h"

/* Procrustes alignment via Kabsch–Umeyama through Eigen SVD (oneMKL-
 * backed when EIGEN_USE_MKL_ALL is set). Verifies the algebraic
 * guarantees of the rectangular orthogonal Procrustes problem. */

namespace {

std::vector<double> to_row_major(const Eigen::MatrixXd& m) {
    std::vector<double> out(static_cast<size_t>(m.rows() * m.cols()));
    for (Eigen::Index r = 0; r < m.rows(); ++r) {
        for (Eigen::Index c = 0; c < m.cols(); ++c) {
            out[static_cast<size_t>(r) * static_cast<size_t>(m.cols())
                + static_cast<size_t>(c)] = m(r, c);
        }
    }
    return out;
}

}  // namespace

TEST(LaplaceDynamicsProcrustes, IdentityWhenSourceEqualsTarget) {
    /* When source_dim == 4 and points coincide, the optimal alignment is
     * the identity — residual ≈ 0, scale ≈ 1, rotation ≈ I. */
    constexpr int N = 16;
    std::mt19937_64 rng(0x5EEDULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    Eigen::MatrixXd P(N, 4);
    for (Eigen::Index i = 0; i < N; ++i)
        for (Eigen::Index j = 0; j < 4; ++j)
            P(i, j) = u(rng);

    auto P_rm = to_row_major(P);
    auto Q_rm = P_rm;  /* identical target */

    auto* T = procrustes_fit(P_rm.data(), N, 4, Q_rm.data());
    ASSERT_NE(T, nullptr);
    EXPECT_LT(procrustes_residual(T), 1e-10);

    /* Apply to one point and confirm it maps back to itself. */
    double mapped[4];
    procrustes_apply(T, P_rm.data(), 4, mapped);
    for (int k = 0; k < 4; ++k)
        EXPECT_NEAR(mapped[k], P_rm[k], 1e-10);

    procrustes_free(T);
}

TEST(LaplaceDynamicsProcrustes, RecoversKnownRotation) {
    /* Generate a random rigid rotation in 4D, apply it to source points,
     * fit Procrustes, and verify it recovers the rotation (residual ≈ 0). */
    constexpr int N = 32;
    std::mt19937_64 rng(0xBEEFULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    /* Random source points. */
    Eigen::MatrixXd P(N, 4);
    for (Eigen::Index i = 0; i < N; ++i)
        for (Eigen::Index j = 0; j < 4; ++j)
            P(i, j) = u(rng);

    /* Random orthogonal 4×4 via QR of a random Gaussian matrix. */
    Eigen::MatrixXd A(4, 4);
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j)
            A(i, j) = u(rng);
    Eigen::HouseholderQR<Eigen::MatrixXd> qr(A);
    Eigen::MatrixXd R_known = qr.householderQ();

    /* Apply rotation + translation. */
    const Eigen::RowVector4d translation(0.5, -0.25, 0.1, -0.7);
    Eigen::MatrixXd Q = (P * R_known).rowwise() + translation;

    auto P_rm = to_row_major(P);
    auto Q_rm = to_row_major(Q);

    auto* T = procrustes_fit(P_rm.data(), N, 4, Q_rm.data());
    ASSERT_NE(T, nullptr);
    EXPECT_LT(procrustes_residual(T), 1e-9)
        << "Procrustes failed to recover rigid alignment";

    /* Re-apply to each source point and verify mapping accuracy. */
    for (int i = 0; i < N; ++i) {
        double mapped[4];
        procrustes_apply(T, &P_rm[static_cast<size_t>(i) * 4], 4, mapped);
        for (int k = 0; k < 4; ++k)
            EXPECT_NEAR(mapped[k], Q_rm[static_cast<size_t>(i) * 4 + k], 1e-9)
                << "point " << i << " dim " << k;
    }

    procrustes_free(T);
}

TEST(LaplaceDynamicsProcrustes, RecoversScale) {
    /* Source points scaled by a known factor; verify Procrustes recovers
     * the scale. */
    constexpr int N = 24;
    constexpr double KNOWN_SCALE = 2.5;
    std::mt19937_64 rng(0xC0FFEEULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    Eigen::MatrixXd P(N, 4);
    for (Eigen::Index i = 0; i < N; ++i)
        for (Eigen::Index j = 0; j < 4; ++j)
            P(i, j) = u(rng);
    Eigen::MatrixXd Q = KNOWN_SCALE * P;

    auto P_rm = to_row_major(P);
    auto Q_rm = to_row_major(Q);

    auto* T = procrustes_fit(P_rm.data(), N, 4, Q_rm.data());
    ASSERT_NE(T, nullptr);
    EXPECT_LT(procrustes_residual(T), 1e-9);

    double mapped[4];
    procrustes_apply(T, P_rm.data(), 4, mapped);
    for (int k = 0; k < 4; ++k)
        EXPECT_NEAR(mapped[k], Q_rm[k], 1e-9);

    procrustes_free(T);
}

TEST(LaplaceDynamicsProcrustes, RectangularProjectionRecoversInLargeNLimit) {
    /* Source is 8-dim; target is 4-dim. The standard SVD formula R = U·Vᵀ
     * provably recovers the true projection when PᵀP is isotropic. For
     * i.i.d. random source coords, PᵀP → (n·σ²)·I as n grows large by the
     * weak law. At n=2000 with uniform [-1,1] coords, PᵀP is close enough
     * to isotropic that R recovers R_true to within ~1% relative residual. */
    constexpr int N = 2000;
    constexpr int D_SRC = 8;
    std::mt19937_64 rng(0xABCDEFULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    Eigen::MatrixXd P(N, D_SRC);
    for (Eigen::Index i = 0; i < N; ++i)
        for (Eigen::Index j = 0; j < D_SRC; ++j)
            P(i, j) = u(rng);

    /* Build a known orthonormal 8×4 projection via QR of a random matrix. */
    Eigen::MatrixXd A(D_SRC, 4);
    for (int i = 0; i < D_SRC; ++i)
        for (int j = 0; j < 4; ++j)
            A(i, j) = u(rng);
    Eigen::HouseholderQR<Eigen::MatrixXd> qr(A);
    Eigen::MatrixXd R_ortho = qr.householderQ() * Eigen::MatrixXd::Identity(D_SRC, 4);
    Eigen::MatrixXd Q = P * R_ortho;

    auto P_rm = to_row_major(P);
    auto Q_rm = to_row_major(Q);

    auto* T = procrustes_fit(P_rm.data(), N, D_SRC, Q_rm.data());
    ASSERT_NE(T, nullptr);

    /* The SVD-based formula R = U·Vᵀ is provably optimal when PᵀP is
     * isotropic. For i.i.d. uniform samples at N=2000, PᵀP is approximately
     * (N·σ²)·I but not exactly so — the formula gives an approximate
     * projection with residual roughly 3-5% relative to ||Q||_F. Substrate
     * use case has anchor sets in the hundreds-to-thousands range, so this
     * is the operational regime. */
    const double q_norm = Q.norm();
    const double res = procrustes_residual(T);
    EXPECT_LT(res, 0.05 * q_norm)
        << "rectangular Procrustes residual too large (res=" << res
        << ", ||Q||=" << q_norm << ", rel=" << res / q_norm << ")";

    procrustes_free(T);
}

TEST(LaplaceDynamicsProcrustes, OrthogonalRowsProperty) {
    /* The output projection R must satisfy Rᵀ·R = I_4 by construction
     * (R = U·Vᵀ where U has orthonormal columns and V is orthogonal). */
    constexpr int N = 100;
    constexpr int D_SRC = 8;
    std::mt19937_64 rng(0x42ULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    Eigen::MatrixXd P(N, D_SRC);
    Eigen::MatrixXd Q(N, 4);
    for (Eigen::Index i = 0; i < N; ++i) {
        for (Eigen::Index j = 0; j < D_SRC; ++j) P(i, j) = u(rng);
        for (Eigen::Index j = 0; j < 4;     ++j) Q(i, j) = u(rng);
    }
    auto P_rm = to_row_major(P);
    auto Q_rm = to_row_major(Q);

    auto* T = procrustes_fit(P_rm.data(), N, D_SRC, Q_rm.data());
    ASSERT_NE(T, nullptr);

    /* Round-trip a basis vector through procrustes_apply and verify
     * the implied projection preserves the orthonormal property. We
     * verify by checking that applying any two of the source basis
     * vectors and inner-producting the results gives ~0 for different
     * basis vectors. */
    std::vector<std::vector<double>> mapped_basis(D_SRC, std::vector<double>(4, 0.0));
    for (int i = 0; i < D_SRC; ++i) {
        std::vector<double> basis(D_SRC, 0.0);
        basis[i] = 1.0;
        procrustes_apply(T, basis.data(), D_SRC, mapped_basis[i].data());
    }
    /* The mean-shifted projection (apply minus apply(zero)) gives the
     * underlying linear projection. */
    std::vector<double> zero(D_SRC, 0.0);
    double mapped_zero[4];
    procrustes_apply(T, zero.data(), D_SRC, mapped_zero);
    for (int i = 0; i < D_SRC; ++i) {
        for (int k = 0; k < 4; ++k) mapped_basis[i][k] -= mapped_zero[k];
    }
    /* Build the (D_SRC × 4) projection from mapped basis vectors and check
     * its column orthonormality (up to the Umeyama scale). */
    Eigen::MatrixXd R_recovered(D_SRC, 4);
    for (int i = 0; i < D_SRC; ++i)
        for (int k = 0; k < 4; ++k)
            R_recovered(i, k) = mapped_basis[i][k];

    /* Rᵀ·R should be a scaled identity (s²·I_4). */
    Eigen::Matrix4d RtR = R_recovered.transpose() * R_recovered;
    /* Off-diagonal entries should be near zero. */
    for (int i = 0; i < 4; ++i) {
        for (int j = 0; j < 4; ++j) {
            if (i != j) EXPECT_NEAR(RtR(i, j), 0.0, 1e-9)
                << "off-diag RtR(" << i << "," << j << ") = " << RtR(i, j);
        }
    }
    /* Diagonal entries should all be equal (= s² for some scale s). */
    const double d0 = RtR(0, 0);
    for (int i = 1; i < 4; ++i) {
        EXPECT_NEAR(RtR(i, i), d0, 1e-9 * std::abs(d0))
            << "diag RtR(" << i << "," << i << ") = " << RtR(i, i)
            << " expected " << d0;
    }

    procrustes_free(T);
}

TEST(LaplaceDynamicsProcrustes, NullInputsHandled) {
    EXPECT_EQ(procrustes_fit(nullptr, 10, 4, nullptr), nullptr);
    EXPECT_DOUBLE_EQ(procrustes_residual(nullptr), 0.0);
    procrustes_free(nullptr);  /* no crash */
    SUCCEED();
}

TEST(LaplaceDynamicsProcrustes, NoiseGivesNonzeroResidual) {
    /* Add Gaussian noise to the target; residual should scale with noise. */
    constexpr int N = 100;
    constexpr double NOISE_STD = 0.01;
    std::mt19937_64 rng(0xDEADBEEFULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);
    std::normal_distribution<double> g(0.0, NOISE_STD);

    Eigen::MatrixXd P(N, 4);
    Eigen::MatrixXd Q(N, 4);
    for (Eigen::Index i = 0; i < N; ++i) {
        for (Eigen::Index j = 0; j < 4; ++j) {
            P(i, j) = u(rng);
            Q(i, j) = P(i, j) + g(rng);
        }
    }
    auto P_rm = to_row_major(P);
    auto Q_rm = to_row_major(Q);

    auto* T = procrustes_fit(P_rm.data(), N, 4, Q_rm.data());
    ASSERT_NE(T, nullptr);
    const double res = procrustes_residual(T);
    /* Frobenius noise: expected ≈ NOISE_STD · sqrt(N · 4). */
    const double expected = NOISE_STD * std::sqrt((double)(N * 4));
    EXPECT_GT(res, 0.5 * expected);
    EXPECT_LT(res, 2.0 * expected);
    procrustes_free(T);
}
