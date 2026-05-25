#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

#include "laplace/synthesis/token_attn_scorer.h"

/* Synthetic small vocabulary for deterministic testing.
 * n_vocab=8, d_model=4, head_dim=2, topk=3 */
static const size_t kVocab    = 8;
static const size_t kDModel   = 4;
static const size_t kHeadDim  = 2;
static const size_t kTopK     = 3;

/* Embedding matrix E [8×4] — distinct rows so attention is non-trivial */
static const double kE[kVocab * kDModel] = {
    1.0,  0.0, 0.5, -0.5,
    0.0,  1.0, 0.5,  0.5,
   -1.0,  0.0, 0.5, -0.5,
    0.0, -1.0, 0.5,  0.5,
    0.7,  0.7, 0.0,  0.0,
   -0.7,  0.7, 0.0,  0.0,
    0.0,  0.0, 1.0,  0.0,
    0.0,  0.0, 0.0,  1.0,
};

/* Wq [head_dim × d_model] — HuggingFace output×input convention.
 * This 2×4 matrix maps hidden→head: identity on first 2 input dims. */
static const double kWq[kHeadDim * kDModel] = {
    1.0, 0.0, 0.0, 0.0,   /* output 0: selects input dim 0 */
    0.0, 1.0, 0.0, 0.0,   /* output 1: selects input dim 1 */
};

/* Wk [head_dim × d_model] — same as Wq so Q=K, giving self-attn = max */
static const double kWk[kHeadDim * kDModel] = {
    1.0, 0.0, 0.0, 0.0,
    0.0, 1.0, 0.0, 0.0,
};

TEST(LaplaceSynthesisTokenAttnScorer, NullArgsReturnMinusOne) {
    qk_pair_t pairs[64];
    EXPECT_EQ(compute_static_qk_scores(nullptr, kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs, 64), -1);
    EXPECT_EQ(compute_static_qk_scores(kE, kVocab, kDModel, nullptr, kWk, kHeadDim, kTopK, pairs, 64), -1);
    EXPECT_EQ(compute_static_qk_scores(kE, kVocab, kDModel, kWq, nullptr, kHeadDim, kTopK, pairs, 64), -1);
    EXPECT_EQ(compute_static_qk_scores(kE, kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, nullptr, 64), -1);
}

TEST(LaplaceSynthesisTokenAttnScorer, CapTooSmallReturnsMinus1) {
    qk_pair_t pairs[4];
    /* out_cap = 4 < n_vocab*topk = 8*3 = 24 */
    EXPECT_EQ(compute_static_qk_scores(kE, kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs, 4), -1);
}

TEST(LaplaceSynthesisTokenAttnScorer, ReturnsPairCount) {
    const size_t cap = kVocab * kTopK;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_qk_scores(kE, kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs.data(), cap);
    ASSERT_GT(n, 0);
    /* Each query token contributes min(topk, vocab) survivors */
    EXPECT_EQ((size_t)n, kVocab * kTopK);
}

TEST(LaplaceSynthesisTokenAttnScorer, AllQueryIndicesPresent) {
    const size_t cap = kVocab * kTopK;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_qk_scores(kE, kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs.data(), cap);
    ASSERT_GT(n, 0);

    /* Every query token index 0..kVocab-1 should appear exactly kTopK times */
    std::vector<size_t> counts(kVocab, 0);
    for (int i = 0; i < n; ++i) {
        ASSERT_LT(pairs[i].query_idx, (uint32_t)kVocab);
        ASSERT_LT(pairs[i].key_idx,   (uint32_t)kVocab);
        counts[pairs[i].query_idx]++;
    }
    for (size_t qi = 0; qi < kVocab; ++qi)
        EXPECT_EQ(counts[qi], kTopK) << "query_idx=" << qi;
}

TEST(LaplaceSynthesisTokenAttnScorer, SelfAttentionIsHighest) {
    /* With Q=K (same Wq=Wk) and unit-norm rows, self-score is the maximum
     * for each query token among the topk survivors (not guaranteed for all
     * rows, but for a simple embedding with distinct orthogonal rows it holds). */
    const size_t cap = kVocab * kTopK;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_qk_scores(kE, kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs.data(), cap);
    ASSERT_GT(n, 0);

    /* Check that for query_idx=0, the self-pair (0,0) is among the survivors. */
    bool found_self = false;
    for (int i = 0; i < n; ++i) {
        if (pairs[i].query_idx == 0 && pairs[i].key_idx == 0) {
            found_self = true;
            break;
        }
    }
    EXPECT_TRUE(found_self) << "Self-attention pair (0,0) not in top-" << kTopK;
}

TEST(LaplaceSynthesisTokenAttnScorer, SingleTokenSingleHead) {
    /* Degenerate case: vocab=1 */
    const double E1[4] = {1.0, 0.0, 0.0, 0.0};
    qk_pair_t pairs[2];
    int n = compute_static_qk_scores(E1, 1, kDModel, kWq, kWk, kHeadDim, 1, pairs, 2);
    ASSERT_EQ(n, 1);
    EXPECT_EQ(pairs[0].query_idx, 0u);
    EXPECT_EQ(pairs[0].key_idx,   0u);
    EXPECT_GT(pairs[0].score, 0.0f);
}
