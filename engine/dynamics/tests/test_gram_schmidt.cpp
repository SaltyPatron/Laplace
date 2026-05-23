#include <gtest/gtest.h>

#include <Eigen/Core>
#include <cmath>
#include <random>
#include <vector>

#include "laplace/dynamics/gram_schmidt.h"

/* Gram-Schmidt orthonormalization via Eigen HouseholderQR (oneMKL-
 * backed when EIGEN_USE_MKL_ALL is set). Verifies orthonormality of the
 * output basis and numerical stability on ill-conditioned input. */

namespace {

void verify_orthonormal(const double* vectors, std::size_t n_vecs, std::size_t dim,
                        double tol) {
    for (std::size_t i = 0; i < n_vecs; ++i) {
        /* Each row should have unit norm. */
        double norm_sq = 0.0;
        for (std::size_t k = 0; k < dim; ++k) {
            const double v = vectors[i * dim + k];
            norm_sq += v * v;
        }
        EXPECT_NEAR(std::sqrt(norm_sq), 1.0, tol)
            << "vector " << i << " norm = " << std::sqrt(norm_sq);

        /* Cross-products with other rows should be ~0. */
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

}  // namespace

TEST(LaplaceDynamicsGramSchmidt, IdentityIsAlreadyOrthonormal) {
    /* Standard basis e1, e2, e3 in R^3 — already orthonormal. */
    double vecs[9] = {
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 3, 3));
    verify_orthonormal(vecs, 3, 3, 1e-13);
}

TEST(LaplaceDynamicsGramSchmidt, OrthonormalizesArbitraryBasis) {
    /* Non-orthogonal but linearly independent input. */
    double vecs[9] = {
        1, 1, 0,
        1, 0, 1,
        0, 1, 1,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 3, 3));
    verify_orthonormal(vecs, 3, 3, 1e-13);
}

TEST(LaplaceDynamicsGramSchmidt, FewerVectorsThanDim) {
    /* 2 vectors in 4D — should produce 2 orthonormal vectors. */
    double vecs[8] = {
        1, 2, 3, 4,
        5, 6, 7, 8,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 2, 4));
    verify_orthonormal(vecs, 2, 4, 1e-13);
}

TEST(LaplaceDynamicsGramSchmidt, RejectsMoreVecsThanDim) {
    /* 4 vectors in 3D — impossible orthonormality. */
    double vecs[12] = {
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
        1, 1, 1,
    };
    EXPECT_EQ(-2, gram_schmidt_orthonormalize(vecs, 4, 3));
}

TEST(LaplaceDynamicsGramSchmidt, DetectsRankDeficiency) {
    /* Three vectors where the third is a linear combination of the first two. */
    double vecs[9] = {
        1, 0, 0,
        0, 1, 0,
        1, 1, 0,  /* = v0 + v1 */
    };
    EXPECT_EQ(-4, gram_schmidt_orthonormalize(vecs, 3, 3));
}

TEST(LaplaceDynamicsGramSchmidt, NullInputReturnsError) {
    EXPECT_EQ(-1, gram_schmidt_orthonormalize(nullptr, 3, 3));
}

TEST(LaplaceDynamicsGramSchmidt, ZeroSizeIsNoOp) {
    double vecs[4] = {1.0, 2.0, 3.0, 4.0};
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 0, 4));
    /* Buffer untouched on n_vecs == 0. */
    EXPECT_DOUBLE_EQ(vecs[0], 1.0);
    EXPECT_DOUBLE_EQ(vecs[1], 2.0);
}

TEST(LaplaceDynamicsGramSchmidt, NumericallyStableOnIllConditionedBasis) {
    /* Build an ill-conditioned basis where naive Gram-Schmidt would lose
     * orthogonality. Three nearly-parallel vectors in R^3 perturbed
     * slightly off-axis. HouseholderQR remains stable; classical/modified
     * Gram-Schmidt would fail this. */
    const double eps = 1e-7;
    double vecs[9] = {
        1.0, eps,       eps,
        1.0, eps + eps, eps,
        1.0, eps,       eps + eps,
    };
    EXPECT_EQ(0, gram_schmidt_orthonormalize(vecs, 3, 3));
    verify_orthonormal(vecs, 3, 3, 1e-10);  /* tighter than classical GS could achieve */
}

TEST(LaplaceDynamicsGramSchmidt, PreservesRowSpan) {
    /* The orthonormalized basis must span the same subspace as the input.
     * Verified by projecting the input back onto the output basis and
     * confirming the residual is ~0. */
    constexpr std::size_t N = 5;
    constexpr std::size_t D = 8;
    std::mt19937_64 rng(0xCAFEULL);
    std::uniform_real_distribution<double> u(-1.0, 1.0);

    std::vector<double> input(N * D);
    for (std::size_t i = 0; i < N * D; ++i) input[i] = u(rng);

    std::vector<double> ortho = input;
    ASSERT_EQ(0, gram_schmidt_orthonormalize(ortho.data(), N, D));

    /* For each input vector v_i, project onto the orthonormal basis:
     * v_i ≈ Σ_j <v_i, q_j> · q_j. Residual should be ~0 since v_i lies
     * in the span of {q_0, ..., q_{N-1}}. */
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
