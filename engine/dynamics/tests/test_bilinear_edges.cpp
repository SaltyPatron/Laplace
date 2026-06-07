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
                                 rows.data(), cols.data(), vals.data(),
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
                                         rows.data(), cols.data(), vals.data(),
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
                                 rows.data(), cols.data(), vals.data(), 1, &cnt, &ov);
    EXPECT_EQ(0, rc);
    EXPECT_EQ(1, ov);
    EXPECT_EQ(1u, cnt);
}

TEST(BilinearEdges, BadArgs) {
    double x = 1.0; int ri; int ci; double vi; size_t cnt; int ov;
    EXPECT_EQ(-1, bilinear_edges_tile(nullptr, 0, 1, &x, 1, 1, 0.0, &ri, &ci, &vi, 1, &cnt, &ov));
    EXPECT_EQ(-1, bilinear_edges_tile(&x, 1, 1, &x, 1, 1, 0.0, &ri, &ci, &vi, 1, &cnt, &ov));
}
