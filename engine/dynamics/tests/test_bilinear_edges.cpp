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

TEST(BilinearEdges, AdmittedPairsCalibrateAgainstExactCircuitRms) {
    // L R^T = [[ 1, -1], [ 2, -2]], so RMS = sqrt(10 / 4).
    const double L[] = {1.0, 0.0, 2.0, 0.0};
    const double R[] = {1.0, 0.0, -1.0, 0.0};
    const int rows[] = {0, 0, 1};
    const int cols[] = {0, 1, 0};
    int64_t scores[3]{};
    int16_t outcomes[3]{};
    double arena = 0.0;

    ASSERT_EQ(0, bilinear_candidates_calibrate(
        L, 2, R, 2, 2, rows, cols, 3, scores, outcomes, &arena));
    EXPECT_NEAR(std::sqrt(2.5), arena, 1e-12);
    EXPECT_EQ((int64_t)std::llround(
        0.5 * (1.0 + std::tanh(1.0 / std::sqrt(2.5))) * 1e9), scores[0]);
    EXPECT_GT(scores[0], 500000000);
    EXPECT_LT(scores[1], 500000000);
    EXPECT_EQ(2, outcomes[0]);
    EXPECT_EQ(0, outcomes[1]);
    EXPECT_GT(scores[2], scores[0]);
}

TEST(BilinearEdges, ZeroEnergyCircuitIsUnknownAndInvalidIndicesFail) {
    const double Z[] = {0.0, 0.0};
    int rows[] = {0};
    int cols[] = {0};
    int64_t score{};
    int16_t outcome{};
    double arena{};
    ASSERT_EQ(0, bilinear_candidates_calibrate(
        Z, 1, Z, 1, 2, rows, cols, 1, &score, &outcome, &arena));
    EXPECT_EQ(0.0, arena);
    EXPECT_EQ(500000000, score);
    EXPECT_EQ(1, outcome);

    rows[0] = 1;
    EXPECT_EQ(-1, bilinear_candidates_calibrate(
        Z, 1, Z, 1, 2, rows, cols, 1, &score, &outcome, &arena));
}

TEST(BilinearEdges, ArenaCanBeReusedAcrossCompleteCandidatePages) {
    const double left[] = { 1.0, 0.0, 0.0, 2.0, -1.0, 1.0 };
    const double right[] = { 1.0, 1.0, -1.0, 0.0, 0.5, -0.5 };
    const int rows[] = { 0, 1, 2 };
    const int cols[] = { 0, 2, 1 };
    int64_t combined_scores[3]{};
    int16_t combined_outcomes[3]{};
    double combined_arena{};
    ASSERT_EQ(0, bilinear_candidates_calibrate(
        left, 3, right, 3, 2, rows, cols, 3,
        combined_scores, combined_outcomes, &combined_arena));

    double arena{};
    ASSERT_EQ(0, bilinear_arena_rms(left, 3, right, 3, 2, &arena));
    EXPECT_DOUBLE_EQ(combined_arena, arena);
    int64_t paged_scores[3]{};
    int16_t paged_outcomes[3]{};
    ASSERT_EQ(0, bilinear_candidates_calibrate_at_arena(
        left, 3, right, 3, 2, rows, cols, 1, arena,
        paged_scores, paged_outcomes));
    ASSERT_EQ(0, bilinear_candidates_calibrate_at_arena(
        left, 3, right, 3, 2, rows + 1, cols + 1, 2, arena,
        paged_scores + 1, paged_outcomes + 1));
    for (int i = 0; i < 3; ++i) {
        EXPECT_EQ(combined_scores[i], paged_scores[i]);
        EXPECT_EQ(combined_outcomes[i], paged_outcomes[i]);
    }
}

TEST(BilinearEdges, ContractedClaimsAreInvariantToCanonicalBasisGauge) {
    const double left[] = { 1, 2, 3, 4, -2, 1 };
    const double right[] = { 2, -1, 0.5, 3, 1, 1 };
    // Same factors after the orthogonal basis map (x,y) -> (-y,x).
    const double left_rotated[] = { -2, 1, -4, 3, -1, -2 };
    const double right_rotated[] = { 1, 2, -3, 0.5, -1, 1 };
    const int rows[] = { 0, 1, 2 };
    const int cols[] = { 2, 0, 1 };
    int64_t scores[3]{}, rotated_scores[3]{};
    int16_t outcomes[3]{}, rotated_outcomes[3]{};
    double arena{}, rotated_arena{};
    ASSERT_EQ(0, bilinear_candidates_calibrate(
        left, 3, right, 3, 2, rows, cols, 3, scores, outcomes, &arena));
    ASSERT_EQ(0, bilinear_candidates_calibrate(
        left_rotated, 3, right_rotated, 3, 2, rows, cols, 3,
        rotated_scores, rotated_outcomes, &rotated_arena));
    EXPECT_NEAR(arena, rotated_arena, 1e-12);
    for (int i = 0; i < 3; ++i) {
        EXPECT_EQ(scores[i], rotated_scores[i]);
        EXPECT_EQ(outcomes[i], rotated_outcomes[i]);
    }
}

TEST(BilinearEdges, TransposeColumnBlockExtractsRightFactor) {
    const float matrix[] = {
        1, 2, 3, 4,
        5, 6, 7, 8,
        9, 10, 11, 12,
    };
    float out[6]{};
    ASSERT_EQ(0, transpose_column_block_f(matrix, 3, 4, 1, 2, out));
    const float expected[] = { 2, 6, 10, 3, 7, 11 };
    for (int i = 0; i < 6; ++i) EXPECT_EQ(expected[i], out[i]);
}

TEST(BilinearEdges, CanonicalAliasSetsAverageEveryTokenAndIgnoreEnumerationOrder) {
    const float embeddings[] = {
         1, 0,
        -1, 0,
         0, 2,
         0, 2,
    };
    const int token_rows[] = {0, 1, 2, 3};
    const int entities[] = {0, 0, 1, 1};
    const int permuted_token_rows[] = {3, 1, 0, 2};
    const int permuted_entities[] = {1, 0, 0, 1};
    const int rows[] = {0, 1};
    const int cols[] = {1, 1};

    bilinear_contraction_context_t* a = nullptr;
    bilinear_contraction_context_t* b = nullptr;
    double arena_a{}, arena_b{};
    size_t bytes_a{}, bytes_b{};
    ASSERT_EQ(0, bilinear_direct_contraction_create(
        embeddings, embeddings, 4, 2, token_rows, entities, 4, 2,
        &a, &arena_a, &bytes_a));
    ASSERT_EQ(0, bilinear_direct_contraction_create(
        embeddings, embeddings, 4, 2, permuted_token_rows, permuted_entities, 4, 2,
        &b, &arena_b, &bytes_b));
    ASSERT_NE(nullptr, a);
    ASSERT_NE(nullptr, b);
    int64_t scores_a[2]{}, scores_b[2]{};
    int16_t outcomes_a[2]{}, outcomes_b[2]{};
    ASSERT_EQ(0, bilinear_contraction_candidates_calibrate(
        a, rows, cols, 2, scores_a, outcomes_a));
    ASSERT_EQ(0, bilinear_contraction_candidates_calibrate(
        b, rows, cols, 2, scores_b, outcomes_b));
    EXPECT_DOUBLE_EQ(2.0, arena_a);
    EXPECT_DOUBLE_EQ(arena_a, arena_b);
    EXPECT_EQ(500000000, scores_a[0]); // [1,0] and [-1,0] cancel as one entity set.
    EXPECT_EQ(1, outcomes_a[0]);
    EXPECT_GT(scores_a[1], 500000000);
    EXPECT_EQ(scores_a[0], scores_b[0]);
    EXPECT_EQ(scores_a[1], scores_b[1]);
    EXPECT_EQ(outcomes_a[0], outcomes_b[0]);
    EXPECT_EQ(outcomes_a[1], outcomes_b[1]);
    EXPECT_EQ(bytes_a, bytes_b);
    bilinear_contraction_free(a);
    bilinear_contraction_free(b);
}

TEST(BilinearEdges, WideProjectionContractsBeforeVocabularyAndMatchesExplicitFactors) {
    const float embeddings[] = {
        1, 0,
        0, 1,
        1, 1,
    };
    const int token_rows[] = {0, 1, 2};
    const int entities[] = {0, 1, 2};
    // rank=4 > augmented input dimension=3 selects the FFN contraction path.
    const float left_weight[] = {
        1, 0,
        0, 1,
        1, 1,
        2, -1,
    };
    const float right_weight[] = {
        2, 0,
        0, 3,
        1, -1,
        -1, 2,
    };
    const float left_bias[] = {0.5f, 0, -0.5f, 1};
    const float right_bias[] = {0, 0.25f, 0.5f, -1};
    std::vector<double> explicit_left(3 * 4), explicit_right(3 * 4);
    for (size_t row = 0; row < 3; ++row) {
        for (size_t k = 0; k < 4; ++k) {
            explicit_left[row * 4 + k] = left_bias[k];
            explicit_right[row * 4 + k] = right_bias[k];
            for (size_t j = 0; j < 2; ++j) {
                explicit_left[row * 4 + k] += embeddings[row * 2 + j] * left_weight[k * 2 + j];
                explicit_right[row * 4 + k] += embeddings[row * 2 + j] * right_weight[k * 2 + j];
            }
        }
    }
    const int rows[] = {0, 1, 2};
    const int cols[] = {2, 0, 1};
    int64_t expected_scores[3]{};
    int16_t expected_outcomes[3]{};
    double expected_arena{};
    ASSERT_EQ(0, bilinear_candidates_calibrate(
        explicit_left.data(), 3, explicit_right.data(), 3, 4,
        rows, cols, 3, expected_scores, expected_outcomes, &expected_arena));

    bilinear_contraction_context_t* context = nullptr;
    double arena{};
    size_t resident{};
    ASSERT_EQ(0, bilinear_projected_contraction_create(
        embeddings, 3, 2, token_rows, entities, 3, 3,
        left_weight, left_bias, right_weight, right_bias, 4,
        &context, &arena, &resident));
    ASSERT_NE(nullptr, context);
    int64_t scores[3]{};
    int16_t outcomes[3]{};
    ASSERT_EQ(0, bilinear_contraction_candidates_calibrate(
        context, rows, cols, 3, scores, outcomes));
    EXPECT_NEAR(expected_arena, arena, 1e-12);
    for (int i = 0; i < 3; ++i) {
        EXPECT_EQ(expected_scores[i], scores[i]);
        EXPECT_EQ(expected_outcomes[i], outcomes[i]);
    }
    EXPECT_LE(resident, 2u * 3u * 3u * sizeof(double));
    bilinear_contraction_free(context);
}

TEST(BilinearEdges, CircuitAggregationUsesCanonicalGlickoAndIsCircuitOrderInvariant) {
    // Two candidates, three circuit witnesses. Candidate 0 is net supporting;
    // candidate 1 is net refuting. The circuit rows are then permuted together
    // with their opponent states and must classify identically.
    const int64_t scores[] = {
        1000000000LL, 0LL,
         800000000LL, 200000000LL,
         600000000LL, 400000000LL,
    };
    const int64_t ratings[] = {1700000000000LL, 1550000000000LL, 1400000000000LL};
    const int64_t rds[] = {50000000000LL, 120000000000LL, 250000000000LL};
    int64_t folded_scores[2]{};
    int16_t outcomes[2]{};
    ASSERT_EQ(0, model_circuit_calibrate_glicko(
        scores, ratings, rds, 3, 2, folded_scores, outcomes));
    EXPECT_GT(folded_scores[0], 500000000LL);
    EXPECT_LT(folded_scores[1], 500000000LL);
    EXPECT_EQ(2, outcomes[0]);
    EXPECT_EQ(0, outcomes[1]);

    const int64_t permuted_scores[] = {
         600000000LL, 400000000LL,
        1000000000LL, 0LL,
         800000000LL, 200000000LL,
    };
    const int64_t permuted_ratings[] = {1400000000000LL, 1700000000000LL, 1550000000000LL};
    const int64_t permuted_rds[] = {250000000000LL, 50000000000LL, 120000000000LL};
    int64_t permuted_folded_scores[2]{};
    int16_t permuted_outcomes[2]{};
    ASSERT_EQ(0, model_circuit_calibrate_glicko(
        permuted_scores, permuted_ratings, permuted_rds, 3, 2,
        permuted_folded_scores, permuted_outcomes));
    EXPECT_EQ(folded_scores[0], permuted_folded_scores[0]);
    EXPECT_EQ(folded_scores[1], permuted_folded_scores[1]);
    EXPECT_EQ(outcomes[0], permuted_outcomes[0]);
    EXPECT_EQ(outcomes[1], permuted_outcomes[1]);
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
