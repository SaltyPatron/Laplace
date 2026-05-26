#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

#include "laplace/synthesis/token_attn_scorer.h"

/* Synthetic small vocabulary for deterministic testing.
 * n_vocab=8, d_model=4, head_dim=2, topk=3.
 *
 * The scorer's public API takes the embedding matrix as BF16 (the on-disk
 * weight format); Wq/Wk are f32. These helpers build that input from the
 * conceptual f32 test values. */
static const size_t kVocab    = 8;
static const size_t kDModel   = 4;
static const size_t kHeadDim  = 2;
static const size_t kTopK     = 3;

/* float -> BF16 (truncate to the upper 16 bits; exact for these test values). */
static uint16_t f2bf16(float f) {
    uint32_t bits;
    std::memcpy(&bits, &f, sizeof(bits));
    return (uint16_t)(bits >> 16);
}

/* Embedding matrix E [8×4] — distinct rows so attention is non-trivial. */
static const float kE_f[kVocab * kDModel] = {
    1.0f,  0.0f, 0.5f, -0.5f,
    0.0f,  1.0f, 0.5f,  0.5f,
   -1.0f,  0.0f, 0.5f, -0.5f,
    0.0f, -1.0f, 0.5f,  0.5f,
    0.7f,  0.7f, 0.0f,  0.0f,
   -0.7f,  0.7f, 0.0f,  0.0f,
    0.0f,  0.0f, 1.0f,  0.0f,
    0.0f,  0.0f, 0.0f,  1.0f,
};

static std::vector<uint16_t> EbfFrom(const float* f, size_t n) {
    std::vector<uint16_t> v(n);
    for (size_t i = 0; i < n; ++i) v[i] = f2bf16(f[i]);
    return v;
}

/* Wq [head_dim × d_model] — HuggingFace output×input convention.
 * This 2×4 matrix maps hidden→head: identity on first 2 input dims. */
static const float kWq[kHeadDim * kDModel] = {
    1.0f, 0.0f, 0.0f, 0.0f,   /* output 0: selects input dim 0 */
    0.0f, 1.0f, 0.0f, 0.0f,   /* output 1: selects input dim 1 */
};

/* Wk [head_dim × d_model] — same as Wq so Q=K, giving self-attn = max */
static const float kWk[kHeadDim * kDModel] = {
    1.0f, 0.0f, 0.0f, 0.0f,
    0.0f, 1.0f, 0.0f, 0.0f,
};

TEST(LaplaceSynthesisTokenAttnScorer, NullArgsReturnMinusOne) {
    auto E = EbfFrom(kE_f, kVocab * kDModel);
    qk_pair_t pairs[64];
    EXPECT_EQ(compute_static_qk_scores(nullptr, kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs, 64), -1);
    EXPECT_EQ(compute_static_qk_scores(E.data(), kVocab, kDModel, nullptr, kWk, kHeadDim, kTopK, pairs, 64), -1);
    EXPECT_EQ(compute_static_qk_scores(E.data(), kVocab, kDModel, kWq, nullptr, kHeadDim, kTopK, pairs, 64), -1);
    EXPECT_EQ(compute_static_qk_scores(E.data(), kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, nullptr, 64), -1);
}

TEST(LaplaceSynthesisTokenAttnScorer, CapTooSmallReturnsMinus1) {
    auto E = EbfFrom(kE_f, kVocab * kDModel);
    qk_pair_t pairs[4];
    /* out_cap = 4 < n_vocab*topk = 8*3 = 24 */
    EXPECT_EQ(compute_static_qk_scores(E.data(), kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs, 4), -1);
}

TEST(LaplaceSynthesisTokenAttnScorer, ReturnsPairCount) {
    auto E = EbfFrom(kE_f, kVocab * kDModel);
    const size_t cap = kVocab * kTopK;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_qk_scores(E.data(), kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs.data(), cap);
    ASSERT_GT(n, 0);
    /* Each query token contributes min(topk, vocab) survivors */
    EXPECT_EQ((size_t)n, kVocab * kTopK);
}

TEST(LaplaceSynthesisTokenAttnScorer, AllQueryIndicesPresent) {
    auto E = EbfFrom(kE_f, kVocab * kDModel);
    const size_t cap = kVocab * kTopK;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_qk_scores(E.data(), kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs.data(), cap);
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
    /* With Q=K (same Wq=Wk) and unit-norm rows, the self-pair is among the
     * topk survivors for a simple embedding with distinct rows. */
    auto E = EbfFrom(kE_f, kVocab * kDModel);
    const size_t cap = kVocab * kTopK;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_qk_scores(E.data(), kVocab, kDModel, kWq, kWk, kHeadDim, kTopK, pairs.data(), cap);
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

TEST(LaplaceSynthesisTokenAttnScorer, MinimalVocabTwoTokens) {
    /* Smallest admissible vocabulary. The SVD-candidate scorer requires
     * n_vocab >= head_dim (thin SVD needs m >= n) AND n_vocab >= 2 (it draws
     * key candidates from each sign of the principal mode, needing
     * n_vocab/2 >= 1). Single-token is not a supported regime; real models
     * always satisfy n_vocab(32K) >> head_dim(64). */
    const float E2f[2 * kDModel] = {
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
    };
    auto E2 = EbfFrom(E2f, 2 * kDModel);
    const float Wq1[1 * kDModel] = {1.0f, 0.0f, 0.0f, 0.0f};
    const float Wk1[1 * kDModel] = {1.0f, 0.0f, 0.0f, 0.0f};
    qk_pair_t pairs[2];
    int n = compute_static_qk_scores(E2.data(), 2, kDModel, Wq1, Wk1, /*head_dim=*/1, /*topk=*/1, pairs, 2);
    ASSERT_EQ(n, 2);
    std::vector<int> seen(2, 0);
    for (int i = 0; i < n; ++i) { ASSERT_LT(pairs[i].query_idx, 2u); seen[pairs[i].query_idx]++; }
    EXPECT_EQ(seen[0], 1);
    EXPECT_EQ(seen[1], 1);
}
