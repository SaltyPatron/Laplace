#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/dynamics/bilinear_edges.h"

namespace {

double ref(const std::vector<double>& L, const std::vector<double>& R,
           size_t i, size_t j, size_t r) {
    double s = 0.0;
    for (size_t k = 0; k < r; ++k) s += L[i * r + k] * R[j * r + k];
    return s;
}

}

TEST(BilinearEdges, FullBilinearAboveTheta_SignedAndComplete) {
    const size_t nL = 3, nR = 4, r = 2;
    std::vector<double> L = {
        1.0,  0.0,
        0.0,  2.0,
        1.0, -1.0,
    };
    std::vector<double> R = {
         3.0,  0.0,
         0.0,  1.0,
        -2.0,  0.0,
         1.0,  1.0,
    };
    const double theta = 1.5;

    std::vector<int> rows(nL * nR), cols(nL * nR);
    std::vector<double> vals(nL * nR);
    size_t count = 0; int overflow = 1;

    int rc = bilinear_edges_tile(L.data(), 0, nL, R.data(), nR, r, theta,
                                 rows.data(), cols.data(), vals.data(), nullptr,
                                 nL * nR, &count, &overflow);
    ASSERT_EQ(0, rc);
    EXPECT_EQ(0, overflow);

    size_t expected = 0;
    for (size_t i = 0; i < nL; ++i)
        for (size_t j = 0; j < nR; ++j)
            if (std::fabs(ref(L, R, i, j, r)) > theta) ++expected;
    EXPECT_EQ(expected, count);

    for (size_t e = 0; e < count; ++e) {
        double want = ref(L, R, (size_t)rows[e], (size_t)cols[e], r);
        EXPECT_NEAR(want, vals[e], 1e-12);
        EXPECT_GT(std::fabs(vals[e]), theta);
    }
    bool found_neg = false;
    for (size_t e = 0; e < count; ++e)
        if (rows[e] == 0 && cols[e] == 2) { EXPECT_NEAR(-2.0, vals[e], 1e-12); found_neg = true; }
    EXPECT_TRUE(found_neg);
}

TEST(BilinearEdges, RowTilingEqualsSinglePass) {
    const size_t nL = 5, nR = 5, r = 3;
    std::vector<double> L(nL * r), R(nR * r);
    for (size_t i = 0; i < L.size(); ++i) L[i] = std::sin(0.7 * (double)i + 1.0);
    for (size_t i = 0; i < R.size(); ++i) R[i] = std::cos(0.3 * (double)i + 2.0);
    const double theta = 0.2;

    auto run = [&](size_t b0, size_t b1, std::vector<int>& rr, std::vector<int>& cc,
                   std::vector<double>& vv) {
        std::vector<int> rows(nL * nR), cols(nL * nR);
        std::vector<double> vals(nL * nR);
        size_t cnt = 0; int ov = 1;
        EXPECT_EQ(0, bilinear_edges_tile(L.data(), b0, b1, R.data(), nR, r, theta,
                                         rows.data(), cols.data(), vals.data(), nullptr,
                                         nL * nR, &cnt, &ov));
        EXPECT_EQ(0, ov);
        for (size_t e = 0; e < cnt; ++e) { rr.push_back(rows[e]); cc.push_back(cols[e]); vv.push_back(vals[e]); }
    };

    std::vector<int> r1, c1; std::vector<double> v1;
    run(0, nL, r1, c1, v1);

    std::vector<int> r2, c2; std::vector<double> v2;
    run(0, 2, r2, c2, v2); run(2, 3, r2, c2, v2); run(3, nL, r2, c2, v2);

    ASSERT_EQ(r1.size(), r2.size());
    for (size_t e = 0; e < r1.size(); ++e) {
        EXPECT_EQ(r1[e], r2[e]);
        EXPECT_EQ(c1[e], c2[e]);
        EXPECT_DOUBLE_EQ(v1[e], v2[e]);
    }
}

TEST(BilinearEdges, OverflowFlagged) {
    const size_t nL = 2, nR = 2, r = 1;
    std::vector<double> L = {1.0, 1.0};
    std::vector<double> R = {1.0, 1.0};
    std::vector<int> rows(1), cols(1); std::vector<double> vals(1);
    size_t cnt = 99; int ov = 0;
    int rc = bilinear_edges_tile(L.data(), 0, nL, R.data(), nR, r, 0.0,
                                 rows.data(), cols.data(), vals.data(), nullptr, 1, &cnt, &ov);
    EXPECT_EQ(0, rc);
    EXPECT_EQ(1, ov);
    EXPECT_EQ(1u, cnt);
}

TEST(BilinearEdges, BadArgs) {
    double x = 1.0; int ri; int ci; double vi; size_t cnt; int ov;
    EXPECT_EQ(-1, bilinear_edges_tile(nullptr, 0, 1, &x, 1, 1, 0.0, &ri, &ci, &vi, nullptr, 1, &cnt, &ov));
    EXPECT_EQ(-1, bilinear_edges_tile(&x, 1, 1, &x, 1, 1, 0.0, &ri, &ci, &vi, nullptr, 1, &cnt, &ov));
}

TEST(ProjectEmbedding, FloatAndDoubleSrcAgree) {

    const size_t n = 2, d = 3, r = 2;
    std::vector<float> pts  = { 1.0f, 0.0f, 0.5f,   0.0f, 1.0f, -0.5f };
    std::vector<float> W    = { 2.0f, 1.0f, 0.0f,   0.0f, 1.0f,  2.0f };

    std::vector<double> ptsD(pts.begin(), pts.end());

    std::vector<double> outF(n * r), outD(n * r);
    ASSERT_EQ(0, project_embedding  (pts.data(),  n, d, W.data(), r, outF.data()));
    ASSERT_EQ(0, project_embedding_d(ptsD.data(), n, d, W.data(), r, outD.data()));

    // Every value above is a dyadic rational, so both paths are exact and this
    // 1e-12 holds even though project_embedding is SGEMM and project_embedding_d
    // is DGEMM. Kept deliberately: it pins that the fp32 path is exact when the
    // arithmetic is. See NonDyadicInputsAgreeToFp32 for the general invariant.
    for (size_t i = 0; i < n * r; ++i)
        EXPECT_NEAR(outF[i], outD[i], 1e-12);
    EXPECT_NEAR(2.0, outF[0], 1e-5);
    EXPECT_NEAR(1.0, outF[1], 1e-5);
    EXPECT_NEAR(1.0, outF[2], 1e-5);
    EXPECT_NEAR(0.0, outF[3], 1e-5);
}

// The fixture above agrees to 1e-12 because its inputs are exactly
// representable, not because the two paths compute identically -- it would pass
// against a broken implementation just as happily. project_embedding takes
// `const float*` and now SGEMMs them (GH #1023); project_embedding_d takes
// `const double*` and DGEMMs. Once the inputs are NOT dyadic the two disagree at
// fp32 epsilon, which is correct and must be asserted as such, or the next
// person reads 1e-12 as a guarantee the code does not make.
TEST(ProjectEmbedding, NonDyadicInputsAgreeToFp32) {
    const size_t n = 4, d = 8, r = 3;
    std::vector<float> pts(n * d), W(r * d);
    // 1/3, 1/7, pi/10 ... nothing here lands on a binary boundary.
    for (size_t i = 0; i < pts.size(); ++i)
        pts[i] = (float)(((i % 7) + 1) / 3.0 - ((i % 5) + 1) / 7.0);
    for (size_t i = 0; i < W.size(); ++i)
        W[i] = (float)(0.3141592653589793 * ((i % 11) + 1) - ((i % 3) + 1) / 9.0);

    std::vector<double> ptsD(pts.begin(), pts.end());
    std::vector<double> outF(n * r), outD(n * r);
    ASSERT_EQ(0, project_embedding  (pts.data(),  n, d, W.data(), r, outF.data()));
    ASSERT_EQ(0, project_embedding_d(ptsD.data(), n, d, W.data(), r, outD.data()));

    // fp32 has ~1.2e-7 relative precision; d=8 accumulation cannot outrun that
    // by more than a small constant. Tight enough to catch a real defect, loose
    // enough to state the truth about the precision actually in play.
    for (size_t i = 0; i < n * r; ++i) {
        const double scale = std::max(1.0, std::fabs(outD[i]));
        EXPECT_NEAR(outF[i], outD[i], 1e-5 * scale)
            << "index " << i << " fp32=" << outF[i] << " fp64=" << outD[i];
    }
}
