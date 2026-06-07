#include <gtest/gtest.h>

#include <Eigen/Core>
#include <cmath>
#include <random>
#include <vector>

#include "laplace/dynamics/gram_schmidt.h"

namespace {

void verify_orthonormal(const double* vectors, std::size_t n_vecs, std::size_t dim,
                        double tol) {
    for (std::size_t i = 0; i < n_vecs; ++i) {
        double norm_sq = 0.0;
        for (std::size_t k = 0; k < dim; ++k) {
            const double v = vectors[i * dim + k];
            norm_sq += v * v;
        }
        EXPECT_NEAR(std::sqrt(norm_sq), 1.0, tol)
            << "vector " << i << " norm = " << std::sqrt(norm_sq);

        for (std::size_t j = i + 1; j < n_vecs; ++j) {
            double dot = 0.0;
            for (std::size_t k = 0; k < dim; ++k) {
                dot += vectors[i * dim + k] * vectors[j * dim + k];
            }
            EXPECT_NEAR(dot, 0.0, tol)
                << "dot(v" << i << ", v" << j << ") = " << dot;
        }
    }
}

}

TEST(LaplaceDynamicsGramSchmidt, IdentityIsAlreadyOrthonormal) {
    double vecs[9] = {
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 3, 3));
    verify_orthonormal(vecs, 3, 3, 1e-13);
}

TEST(LaplaceDynamicsGramSchmidt, OrthonormalizesArbitraryBasis) {
    double vecs[9] = {
        1, 1, 0,
        1, 0, 1,
        0, 1, 1,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 3, 3));
    verify_orthonormal(vecs, 3, 3, 1e-13);
}

TEST(LaplaceDynamicsGramSchmidt, FewerVectorsThanDim) {
    double vecs[8] = {
        1, 2, 3, 4,
        5, 6, 7, 8,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 2, 4));
    verify_orthonormal(vecs, 2, 4, 1e-13);
}

TEST(LaplaceDynamicsGramSchmidt, RejectsMoreVecsThanDim) {
    double vecs[12] = {
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
        1, 1, 1,
    };
    EXPECT_EQ(-2, gram_schmidt_orthonormalize(vecs, 4, 3));
}

TEST(LaplaceDynamicsGramSchmidt, DetectsRankDeficiency) {
    double vecs[9] = {
        1, 0, 0,
        0, 1, 0,
        1, 1, 0,
    };
    EXPECT_EQ(-4, gram_schmidt_orthonormalize(vecs, 3, 3));
}

TEST(LaplaceDynamicsGramSchmidt, NullInputReturnsError) {
    EXPECT_EQ(-1, gram_schmidt_orthonormalize(nullptr, 3, 3));
}

TEST(LaplaceDynamicsGramSchmidt, ZeroSizeIsNoOp) {
    double vecs[4] = {1.0, 2.0, 3.0, 4.0};
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 0, 4));
    EXPECT_DOUBLE_EQ(vecs[0], 1.0);
    EXPECT_DOUBLE_EQ(vecs[1], 2.0);
}

TEST(LaplaceDynamicsGramSchmidt, NumericallyStableOnIllConditionedBasis) {
    const double eps = 1e-7;
    double vecs[9] = {
        1.0, eps,       eps,
        1.0, eps + eps, eps,
        1.0, eps,       eps + eps,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 3, 3));
    verify_orthonormal(vecs, 3, 3, 1e-10);
}

TEST(LaplaceDynamicsGramSchmidt, PreservesRowSpan) {
    constexpr std::size_t N = 5;
    constexpr std::size_t D = 8;
    std::mt19937_64 rng(0xCAFEULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    std::vector<double> input(N * D);
    for (std::size_t i = 0; i < N * D; ++i) input[i] = u(rng);

    std::vector<double> ortho = input;
    ASSERT_EQ(0, gram_schmidt_orthonormalize(ortho.data(), N, D));

    for (std::size_t i = 0; i < N; ++i) {
        std::vector<double> proj(D, 0.0);
        for (std::size_t j = 0; j < N; ++j) {
            double dot = 0.0;
            for (std::size_t k = 0; k < D; ++k) {
                dot += input[i * D + k] * ortho[j * D + k];
            }
            for (std::size_t k = 0; k < D; ++k) {
                proj[k] += dot * ortho[j * D + k];
            }
        }
        for (std::size_t k = 0; k < D; ++k) {
            EXPECT_NEAR(proj[k], input[i * D + k], 1e-12)
                << "row span not preserved at vector " << i << " dim " << k;
        }
    }
}
