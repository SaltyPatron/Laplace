#include <gtest/gtest.h>

#include <Eigen/Core>
#include <Eigen/Geometry>
#include <cmath>
#include <random>
#include <vector>

#include "laplace/dynamics/procrustes.h"

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

}

TEST(LaplaceDynamicsProcrustes, IdentityWhenSourceEqualsTarget) {
    constexpr int N = 16;
    std::mt19937_64 rng(0x5EEDULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    Eigen::MatrixXd P(N, 4);
    for (Eigen::Index i = 0; i < N; ++i)
        for (Eigen::Index j = 0; j < 4; ++j)
            P(i, j) = u(rng);

    auto P_rm = to_row_major(P);
    auto Q_rm = P_rm;

    auto* T = procrustes_fit(P_rm.data(), N, 4, Q_rm.data());
    ASSERT_NE(T, nullptr);
    EXPECT_LT(procrustes_residual(T), 1e-10);

    double mapped[4];
    procrustes_apply(T, P_rm.data(), 4, mapped);
    for (int k = 0; k < 4; ++k)
        EXPECT_NEAR(mapped[k], P_rm[k], 1e-10);

    procrustes_free(T);
}

TEST(LaplaceDynamicsProcrustes, RecoversKnownRotation) {
    constexpr int N = 32;
    std::mt19937_64 rng(0xBEEFULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    Eigen::MatrixXd P(N, 4);
    for (Eigen::Index i = 0; i < N; ++i)
        for (Eigen::Index j = 0; j < 4; ++j)
            P(i, j) = u(rng);

    Eigen::MatrixXd A(4, 4);
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j)
            A(i, j) = u(rng);
    Eigen::HouseholderQR<Eigen::MatrixXd> qr(A);
    Eigen::MatrixXd R_known = qr.householderQ();

    const Eigen::RowVector4d translation(0.5, -0.25, 0.1, -0.7);
    Eigen::MatrixXd Q = (P * R_known).rowwise() + translation;

    auto P_rm = to_row_major(P);
    auto Q_rm = to_row_major(Q);

    auto* T = procrustes_fit(P_rm.data(), N, 4, Q_rm.data());
    ASSERT_NE(T, nullptr);
    EXPECT_LT(procrustes_residual(T), 1e-9)
        << "Procrustes failed to recover rigid alignment";

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
    constexpr int N = 2000;
    constexpr int D_SRC = 8;
    std::mt19937_64 rng(0xABCDEFULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    Eigen::MatrixXd P(N, D_SRC);
    for (Eigen::Index i = 0; i < N; ++i)
        for (Eigen::Index j = 0; j < D_SRC; ++j)
            P(i, j) = u(rng);

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

    const double q_norm = Q.norm();
    const double res = procrustes_residual(T);
    EXPECT_LT(res, 0.05 * q_norm)
        << "rectangular Procrustes residual too large (res=" << res
        << ", ||Q||=" << q_norm << ", rel=" << res / q_norm << ")";

    procrustes_free(T);
}

TEST(LaplaceDynamicsProcrustes, OrthogonalRowsProperty) {
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

    std::vector<std::vector<double>> mapped_basis(D_SRC, std::vector<double>(4, 0.0));
    for (int i = 0; i < D_SRC; ++i) {
        std::vector<double> basis(D_SRC, 0.0);
        basis[i] = 1.0;
        procrustes_apply(T, basis.data(), D_SRC, mapped_basis[i].data());
    }
    std::vector<double> zero(D_SRC, 0.0);
    double mapped_zero[4];
    procrustes_apply(T, zero.data(), D_SRC, mapped_zero);
    for (int i = 0; i < D_SRC; ++i) {
        for (int k = 0; k < 4; ++k) mapped_basis[i][k] -= mapped_zero[k];
    }
    Eigen::MatrixXd R_recovered(D_SRC, 4);
    for (int i = 0; i < D_SRC; ++i)
        for (int k = 0; k < 4; ++k)
            R_recovered(i, k) = mapped_basis[i][k];

    Eigen::Matrix4d RtR = R_recovered.transpose() * R_recovered;
    for (int i = 0; i < 4; ++i) {
        for (int j = 0; j < 4; ++j) {
            if (i != j) EXPECT_NEAR(RtR(i, j), 0.0, 1e-9)
                << "off-diag RtR(" << i << "," << j << ") = " << RtR(i, j);
        }
    }
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
    procrustes_free(nullptr);
    SUCCEED();
}

TEST(LaplaceDynamicsProcrustes, NoiseGivesNonzeroResidual) {
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
    const double expected = NOISE_STD * std::sqrt((double)(N * 4));
    EXPECT_GT(res, 0.5 * expected);
    EXPECT_LT(res, 2.0 * expected);
    procrustes_free(T);
}
