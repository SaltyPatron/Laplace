#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/dynamics/model_math.h"

namespace {
constexpr double kEps = 1e-12;
}

TEST(ModelMath, CenterColumnsD_ZeroColumnMeans) {
    std::vector<double> m = {
        1.0, 10.0,
        3.0, 20.0,
        5.0, 30.0,
    };
    ASSERT_EQ(center_columns_d(m.data(), 3, 2), 0);
    for (size_t j = 0; j < 2; ++j) {
        double s = m[0 * 2 + j] + m[1 * 2 + j] + m[2 * 2 + j];
        EXPECT_NEAR(s, 0.0, kEps);
    }
    EXPECT_NEAR(m[0], -2.0, kEps);
    EXPECT_NEAR(m[5], 10.0, kEps);
}

TEST(ModelMath, CenterColumnsF_MatchesDoublePath) {
    std::vector<float>  f = { 1.f, 10.f, 3.f, 20.f, 5.f, 30.f };
    std::vector<double> d = { 1.0, 10.0, 3.0, 20.0, 5.0, 30.0 };
    ASSERT_EQ(center_columns_f(f.data(), 3, 2), 0);
    ASSERT_EQ(center_columns_d(d.data(), 3, 2), 0);
    for (size_t i = 0; i < 6; ++i)
        EXPECT_NEAR((double)f[i], d[i], 1e-6);
}

TEST(ModelMath, ScaleCols_FloatAndDouble) {
    std::vector<float> g = { 2.f, 0.5f };
    std::vector<float> mf = { 1.f, 4.f, 3.f, 8.f };
    std::vector<double> md = { 1.0, 4.0, 3.0, 8.0 };
    ASSERT_EQ(scale_cols_f(mf.data(), 2, 2, g.data()), 0);
    ASSERT_EQ(scale_cols_d(md.data(), 2, 2, g.data()), 0);
    const double expect[] = { 2.0, 2.0, 6.0, 4.0 };
    for (size_t i = 0; i < 4; ++i) {
        EXPECT_NEAR((double)mf[i], expect[i], 1e-6);
        EXPECT_NEAR(md[i], expect[i], kEps);
    }
}

TEST(ModelMath, SliceHeadD_ExtractsContiguousSlice) {
    // n=2 rows, fullDim=6 (3 heads x hd=2); slice head 1.
    std::vector<double> full = {
        0, 1, 10, 11, 20, 21,
        2, 3, 12, 13, 22, 23,
    };
    std::vector<double> head(4, -1.0);
    ASSERT_EQ(slice_head_d(full.data(), head.data(), 2, 6, 1, 2), 0);
    EXPECT_NEAR(head[0], 10.0, kEps);
    EXPECT_NEAR(head[1], 11.0, kEps);
    EXPECT_NEAR(head[2], 12.0, kEps);
    EXPECT_NEAR(head[3], 13.0, kEps);
    // Out-of-range head is refused.
    EXPECT_EQ(slice_head_d(full.data(), head.data(), 2, 6, 3, 2), -1);
}

TEST(ModelMath, RowNormsOutD_L2PerRow) {
    std::vector<double> m = { 3.0, 4.0, 0.0, 0.0, 1.0, 1.0 };
    std::vector<double> out(3, -1.0);
    ASSERT_EQ(row_norms_out_d(m.data(), 3, 2, out.data()), 0);
    EXPECT_NEAR(out[0], 5.0, kEps);
    EXPECT_NEAR(out[1], 0.0, kEps);
    EXPECT_NEAR(out[2], std::sqrt(2.0), kEps);
}

TEST(ModelMath, F32ToF64_ExactWiden) {
    std::vector<float> src(10000);
    for (size_t i = 0; i < src.size(); ++i) src[i] = (float)(i * 0.25 - 100.0);
    std::vector<double> dst(src.size(), 0.0);
    ASSERT_EQ(f32_to_f64(src.data(), src.size(), dst.data()), 0);
    for (size_t i = 0; i < src.size(); ++i)
        EXPECT_EQ(dst[i], (double)src[i]);
}

TEST(ModelMath, HypotRowsD_Elementwise) {
    std::vector<double> a = { 3.0, 0.0, 1.0 };
    std::vector<double> b = { 4.0, 2.0, 1.0 };
    std::vector<double> out(3, -1.0);
    ASSERT_EQ(hypot_rows_d(a.data(), b.data(), 3, out.data()), 0);
    EXPECT_NEAR(out[0], 5.0, kEps);
    EXPECT_NEAR(out[1], 2.0, kEps);
    EXPECT_NEAR(out[2], std::sqrt(2.0), kEps);
    // In-place into a (the ETL's salience-combine call shape).
    ASSERT_EQ(hypot_rows_d(a.data(), b.data(), 3, a.data()), 0);
    EXPECT_NEAR(a[0], 5.0, kEps);
}

TEST(ModelMath, FfnActivationNorms_MatchesScalarReference) {
    // n=3 tokens, d=2, interm=4; gated and ungated vs a scalar reference.
    const size_t n = 3, d = 2, I = 4;
    std::vector<double> x = { 0.5, -1.0, 2.0, 0.25, -0.75, 1.5 };
    std::vector<float> up(I * d), gate(I * d);
    for (size_t i = 0; i < I * d; ++i) {
        up[i]   = (float)(0.1 * (double)(i + 1));
        gate[i] = (float)(0.2 * (double)(I * d - i));
    }
    auto silu = [](double v) { return v / (1.0 + std::exp(-v)); };
    auto reference = [&](bool gated, size_t t) {
        double ss = 0.0;
        for (size_t c = 0; c < I; ++c) {
            double u = 0.0, g = 0.0;
            for (size_t k = 0; k < d; ++k) {
                u += x[t * d + k] * (double)up[c * d + k];
                g += x[t * d + k] * (double)gate[c * d + k];
            }
            double a = gated ? u * silu(g) : u;
            ss += a * a;
        }
        return std::sqrt(ss);
    };

    std::vector<double> got(n, -1.0);
    int rc = ffn_activation_norms(x.data(), n, d, up.data(), gate.data(), I, got.data());
    if (rc == -2) GTEST_SKIP() << "MKL unavailable in this build";
    ASSERT_EQ(rc, 0);
    for (size_t t = 0; t < n; ++t)
        EXPECT_NEAR(got[t], reference(true, t), 1e-9);

    ASSERT_EQ(ffn_activation_norms(x.data(), n, d, up.data(), nullptr, I, got.data()), 0);
    for (size_t t = 0; t < n; ++t)
        EXPECT_NEAR(got[t], reference(false, t), 1e-9);
}
