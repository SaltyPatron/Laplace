#include <gtest/gtest.h>

#include <cmath>
#include <vector>

#include "laplace/dynamics/bilinear_edges.h"

/* The faithful contracted-operator kernel: M = Left·Rightᵀ, emit every signed
 * cell above the coherence threshold theta. These prove it's the FULL bilinear
 * (no argmax, no top-k, no a-priori floor), signed, exact (f64 dgemm), and that
 * row-tiling is equivalent to a single pass. */

namespace {

// Brute-force reference: M[i][j] = Σ_k Left[i,k]*Right[j,k].
double ref(const std::vector<double>& L, const std::vector<double>& R,
           size_t i, size_t j, size_t r) {
    double s = 0.0;
    for (size_t k = 0; k < r; ++k) s += L[i * r + k] * R[j * r + k];
    return s;
}

}  // namespace

TEST(BilinearEdges, FullBilinearAboveTheta_SignedAndComplete) {
    // 3 left rows, 4 right rows, r=2. Hand-built so some cells are +, some −.
    const size_t nL = 3, nR = 4, r = 2;
    std::vector<double> L = {
        1.0,  0.0,
        0.0,  2.0,
        1.0, -1.0,
    };
    std::vector<double> R = {
         3.0,  0.0,   //  col0
         0.0,  1.0,   //  col1
        -2.0,  0.0,   //  col2
         1.0,  1.0,   //  col3
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

    // Expected from the reference: keep |M| > 1.5, signed.
    //  M[0] = [3, 0, -2, 1]      -> (0,0,+3), (0,2,-2)
    //  M[1] = [0, 2,  0, 2]      -> (1,1,+2), (1,3,+2)
    //  M[2] = [3,-1, -2, 0]      -> (2,0,+3), (2,2,-2)
    size_t expected = 0;
    for (size_t i = 0; i < nL; ++i)
        for (size_t j = 0; j < nR; ++j)
            if (std::fabs(ref(L, R, i, j, r)) > theta) ++expected;
    EXPECT_EQ(expected, count);

    // Every emitted edge matches the exact bilinear value (signed), and every
    // above-theta reference cell is present — full operator, nothing argmaxed away.
    for (size_t e = 0; e < count; ++e) {
        double want = ref(L, R, (size_t)rows[e], (size_t)cols[e], r);
        EXPECT_NEAR(want, vals[e], 1e-12);
        EXPECT_GT(std::fabs(vals[e]), theta);
    }
    // Confirm a known signed pair is present: (0,2) must be -2 (a repel, kept negative).
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
    run(0, nL, r1, c1, v1);                       // single pass

    std::vector<int> r2, c2; std::vector<double> v2;
    run(0, 2, r2, c2, v2); run(2, 3, r2, c2, v2); run(3, nL, r2, c2, v2);  // 3 tiles

    ASSERT_EQ(r1.size(), r2.size());
    for (size_t e = 0; e < r1.size(); ++e) {
        EXPECT_EQ(r1[e], r2[e]);
        EXPECT_EQ(c1[e], c2[e]);
        EXPECT_DOUBLE_EQ(v1[e], v2[e]);           // bit-identical across tiling
    }
}

TEST(BilinearEdges, OverflowFlagged) {
    const size_t nL = 2, nR = 2, r = 1;
    std::vector<double> L = {1.0, 1.0};
    std::vector<double> R = {1.0, 1.0};
    std::vector<int> rows(1), cols(1); std::vector<double> vals(1);
    size_t cnt = 99; int ov = 0;
    // theta=0 keeps all 4 cells, cap=1 -> overflow.
    int rc = bilinear_edges_tile(L.data(), 0, nL, R.data(), nR, r, 0.0,
                                 rows.data(), cols.data(), vals.data(), 1, &cnt, &ov);
    EXPECT_EQ(0, rc);
    EXPECT_EQ(1, ov);
    EXPECT_EQ(1u, cnt);
}

TEST(BilinearEdges, BadArgs) {
    double x = 1.0; int ri; int ci; double vi; size_t cnt; int ov;
    EXPECT_EQ(-1, bilinear_edges_tile(nullptr, 0, 1, &x, 1, 1, 0.0, &ri, &ci, &vi, 1, &cnt, &ov));
    EXPECT_EQ(-1, bilinear_edges_tile(&x, 1, 1, &x, 1, 1, 0.0, &ri, &ci, &vi, 1, &cnt, &ov)); // empty range
}
