#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <vector>

#include "laplace/synthesis/weight_projection.h"

/* float -> BF16 (truncate upper 16 bits; exact for the small integers below). */
static uint16_t f2bf16(float f) {
    uint32_t b;
    std::memcpy(&b, &f, sizeof(b));
    return (uint16_t)(b >> 16);
}

/* n_vocab=4, d_model=4, n_out=3.
 * E[0] = [1,0,0,0]; W[0]=[2,0,0,0], W[1]=[0,1,0,0], W[2]=[3,0,0,0]
 * => P[0] = [E0·W0, E0·W1, E0·W2] = [2, 0, 3]; |.| ranks: o=2(3) > o=0(2) > o=1(0). */
static const float kE[4 * 4] = {
    1.0f, 0.0f, 0.0f, 0.0f,
    0.0f, 1.0f, 0.0f, 0.0f,
    0.0f, 0.0f, 1.0f, 0.0f,
    0.0f, 0.0f, 0.0f, 1.0f,
};
static const float kW[3 * 4] = {
    2.0f, 0.0f, 0.0f, 0.0f,   /* out 0 */
    0.0f, 1.0f, 0.0f, 0.0f,   /* out 1 */
    3.0f, 0.0f, 0.0f, 0.0f,   /* out 2 */
};

static std::vector<uint16_t> Ebf() {
    std::vector<uint16_t> v(4 * 4);
    for (size_t i = 0; i < v.size(); ++i) v[i] = f2bf16(kE[i]);
    return v;
}

TEST(WeightProjection, NullArgsRejected) {
    auto E = Ebf();
    qk_pair_t pairs[16];
    EXPECT_EQ(compute_static_projection_scores(nullptr, 4, 4, kW, 3, 1, pairs, 16), -1);
    EXPECT_EQ(compute_static_projection_scores(E.data(), 4, 4, nullptr, 3, 1, pairs, 16), -1);
    EXPECT_EQ(compute_static_projection_scores(E.data(), 4, 4, kW, 3, 1, nullptr, 16), -1);
    EXPECT_EQ(compute_static_projection_scores(E.data(), 4, 4, kW, 3, 1, pairs, 2), -1); /* cap too small */
}

TEST(WeightProjection, Top1PicksLargestMagnitude) {
    auto E = Ebf();
    const size_t cap = 4 * 1;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_projection_scores(E.data(), 4, 4, kW, 3, 1, pairs.data(), cap);
    if (n == -2) GTEST_SKIP() << "MKL/BLAS unavailable";
    ASSERT_EQ(n, 4);
    /* token 0's single survivor must be out-dim 2 with value 3. */
    bool found = false;
    for (int i = 0; i < n; ++i)
        if (pairs[i].query_idx == 0) {
            EXPECT_EQ(pairs[i].key_idx, 2u);
            EXPECT_NEAR(pairs[i].score, 3.0f, 1e-3);
            found = true;
        }
    EXPECT_TRUE(found);
}

TEST(WeightProjection, AllTokensPresentTopK) {
    auto E = Ebf();
    const size_t k = 2, cap = 4 * k;
    std::vector<qk_pair_t> pairs(cap);
    int n = compute_static_projection_scores(E.data(), 4, 4, kW, 3, k, pairs.data(), cap);
    if (n == -2) GTEST_SKIP() << "MKL/BLAS unavailable";
    ASSERT_EQ((size_t)n, 4 * k);
    std::vector<int> seen(4, 0);
    for (int i = 0; i < n; ++i) { ASSERT_LT(pairs[i].query_idx, 4u); seen[pairs[i].query_idx]++; }
    for (int t = 0; t < 4; ++t) EXPECT_EQ(seen[t], (int)k) << "token " << t;
}
