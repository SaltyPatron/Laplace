#include <gtest/gtest.h>

#include <cstring>
#include <vector>

#include "laplace/core/merkle_dedup.h"
#include "laplace/core/hash128.h"
#include "laplace/core/tier_tree.h"

namespace {

hash128_t make_hash(uint8_t fill) {
    hash128_t h;
    std::memset(&h, fill, sizeof(h));
    return h;
}

tier_tree_t* sample_tree() {
    tier_tree_t* t = tier_tree_new(8);
    tier_tree_add_leaf(t, 0, 100, 0, 1);
    tier_tree_add_leaf(t, 0, 101, 1, 1);
    tier_tree_add_leaf(t, 0, 102, 2, 1);
    tier_tree_add_leaf(t, 0, 103, 3, 1);
    tier_tree_add_node(t, 1, 0, 2, 0, 2);
    tier_tree_add_node(t, 1, 2, 2, 2, 2);
    tier_tree_add_node(t, 2, 4, 2, 0, 4);
    tier_tree_finalize(t);
    return t;
}

}

TEST(LaplaceCoreMerkleDedup, FilterNovelEmptyInputReturnsZero) {
    size_t out_n = 99;
    EXPECT_EQ(0, merkle_dedup_filter_novel(nullptr, 0, nullptr, 0, nullptr, &out_n));
    EXPECT_EQ(0u, out_n);
}

TEST(LaplaceCoreMerkleDedup, FilterNovelRejectsNullArgs) {
    hash128_t h = make_hash(0);
    uint8_t bm = 0;
    hash128_t out[1];
    size_t n = 0;
    EXPECT_NE(0, merkle_dedup_filter_novel(nullptr, 1, &bm, 8, out, &n));
    EXPECT_NE(0, merkle_dedup_filter_novel(&h, 1, nullptr, 8, out, &n));
    EXPECT_NE(0, merkle_dedup_filter_novel(&h, 1, &bm, 8, nullptr, &n));
    EXPECT_NE(0, merkle_dedup_filter_novel(&h, 1, &bm, 8, out, nullptr));
}

TEST(LaplaceCoreMerkleDedup, FilterNovelRejectsTooSmallBitmap) {
    hash128_t h = make_hash(0);
    uint8_t bm = 0;
    hash128_t out[1];
    size_t n = 0;
    EXPECT_NE(0, merkle_dedup_filter_novel(&h, 1, &bm, 0, out, &n));
}

TEST(LaplaceCoreMerkleDedup, FilterNovelAllAbsentEmitsAll) {
    std::vector<hash128_t> in(5);
    for (uint8_t i = 0; i < 5; ++i) in[i] = make_hash(i);
    uint8_t bm[1] = {0};
    std::vector<hash128_t> out(5);
    size_t out_n = 0;
    ASSERT_EQ(0, merkle_dedup_filter_novel(in.data(), in.size(), bm, 8,
                                            out.data(), &out_n));
    EXPECT_EQ(5u, out_n);
    for (size_t i = 0; i < 5; ++i) {
        EXPECT_TRUE(hash128_equals(&out[i], &in[i])) << "i=" << i;
    }
}

TEST(LaplaceCoreMerkleDedup, FilterNovelAllPresentEmitsZero) {
    std::vector<hash128_t> in(5);
    for (uint8_t i = 0; i < 5; ++i) in[i] = make_hash(i);
    uint8_t bm[1] = {0xFF};
    std::vector<hash128_t> out(5);
    size_t out_n = 99;
    ASSERT_EQ(0, merkle_dedup_filter_novel(in.data(), in.size(), bm, 8,
                                            out.data(), &out_n));
    EXPECT_EQ(0u, out_n);
}

TEST(LaplaceCoreMerkleDedup, FilterNovelPreservesOrder) {
    std::vector<hash128_t> in(8);
    for (uint8_t i = 0; i < 8; ++i) in[i] = make_hash(i + 1);
    uint8_t bm[1] = { 0b01100101 };
    std::vector<hash128_t> out(8);
    size_t out_n = 0;
    ASSERT_EQ(0, merkle_dedup_filter_novel(in.data(), in.size(), bm, 8,
                                            out.data(), &out_n));
    ASSERT_EQ(4u, out_n);
    EXPECT_TRUE(hash128_equals(&out[0], &in[1]));
    EXPECT_TRUE(hash128_equals(&out[1], &in[3]));
    EXPECT_TRUE(hash128_equals(&out[2], &in[4]));
    EXPECT_TRUE(hash128_equals(&out[3], &in[7]));
}

TEST(LaplaceCoreMerkleDedup, FilterNovelWorksAcrossByteBoundaries) {
    std::vector<hash128_t> in(17);
    for (uint8_t i = 0; i < 17; ++i) in[i] = make_hash(i);
    uint8_t bm[3] = { 0b00000001, 0b00000001, 0b00000001 };
    std::vector<hash128_t> out(17);
    size_t out_n = 0;
    ASSERT_EQ(0, merkle_dedup_filter_novel(in.data(), in.size(), bm, 24,
                                            out.data(), &out_n));
    EXPECT_EQ(14u, out_n);
    EXPECT_FALSE(hash128_equals(&out[0], &in[0]));
    EXPECT_TRUE(hash128_equals(&out[0], &in[1]));
    EXPECT_TRUE(hash128_equals(&out[6], &in[7]));
    EXPECT_TRUE(hash128_equals(&out[7], &in[9]));
}

TEST(LaplaceCoreMerkleDedup, FilterNovelLargeBatchPerf) {
    const size_t N = 100'000;
    std::vector<hash128_t> in(N);
    for (size_t i = 0; i < N; ++i) in[i] = make_hash((uint8_t)(i & 0xFF));
    std::vector<uint8_t> bm((N + 7) / 8, 0);
    for (size_t i = 0; i < N; i += 2) bm[i >> 3] |= (uint8_t)(1u << (i & 7));
    std::vector<hash128_t> out(N);
    size_t out_n = 0;
    ASSERT_EQ(0, merkle_dedup_filter_novel(in.data(), N, bm.data(), N,
                                            out.data(), &out_n));
    EXPECT_EQ(N / 2, out_n);
    EXPECT_TRUE(hash128_equals(&out[0], &in[1]));
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitRejectsNullArgs) {
    tier_tree_t* t = sample_tree();
    uint8_t bm[1] = {0};
    uint32_t out[7];
    size_t n = 0;
    EXPECT_NE(0, merkle_dedup_trunk_shortcircuit(nullptr, bm, 7, out, &n));
    EXPECT_NE(0, merkle_dedup_trunk_shortcircuit(t, nullptr, 7, out, &n));
    EXPECT_NE(0, merkle_dedup_trunk_shortcircuit(t, bm, 7, nullptr, &n));
    EXPECT_NE(0, merkle_dedup_trunk_shortcircuit(t, bm, 7, out, nullptr));
    tier_tree_free(t);
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitEmptyTreeIsZero) {
    tier_tree_t* t = tier_tree_new(0);
    uint8_t bm = 0;
    uint32_t out[1];
    size_t n = 99;
    EXPECT_EQ(0, merkle_dedup_trunk_shortcircuit(t, &bm, 0, out, &n));
    EXPECT_EQ(0u, n);
    tier_tree_free(t);
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitAllAbsentEmitsEveryIndex) {
    tier_tree_t* t = sample_tree();
    uint8_t bm[1] = {0};
    uint32_t out[7];
    size_t n = 0;
    ASSERT_EQ(0, merkle_dedup_trunk_shortcircuit(t, bm, 7, out, &n));
    EXPECT_EQ(7u, n);
    for (uint32_t i = 0; i < 7; ++i) EXPECT_EQ(i, out[i]);
    tier_tree_free(t);
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitRootPresentEmitsNothing) {
    tier_tree_t* t = sample_tree();
    uint8_t bm[1] = { (uint8_t)(1u << 6) };
    uint32_t out[7];
    size_t n = 99;
    ASSERT_EQ(0, merkle_dedup_trunk_shortcircuit(t, bm, 7, out, &n));
    EXPECT_EQ(0u, n);
    tier_tree_free(t);
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitInteriorPresentSkipsItsSubtree) {
    tier_tree_t* t = sample_tree();
    uint8_t bm[1] = { (uint8_t)(1u << 4) };
    uint32_t out[7];
    size_t n = 0;
    ASSERT_EQ(0, merkle_dedup_trunk_shortcircuit(t, bm, 7, out, &n));
    ASSERT_EQ(4u, n);
    EXPECT_EQ(2u, out[0]);
    EXPECT_EQ(3u, out[1]);
    EXPECT_EQ(5u, out[2]);
    EXPECT_EQ(6u, out[3]);
    tier_tree_free(t);
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitLeafPresentSkipsOnlyLeaf) {
    tier_tree_t* t = sample_tree();
    uint8_t bm[1] = { (uint8_t)(1u << 1) };
    uint32_t out[7];
    size_t n = 0;
    ASSERT_EQ(0, merkle_dedup_trunk_shortcircuit(t, bm, 7, out, &n));
    ASSERT_EQ(6u, n);
    const uint32_t expected[] = {0, 2, 3, 4, 5, 6};
    for (size_t i = 0; i < 6; ++i) EXPECT_EQ(expected[i], out[i]);
    tier_tree_free(t);
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitMatchesFilterNovelOnSubsetInvariant) {
    tier_tree_t* t = sample_tree();
    uint8_t bm[1] = { (uint8_t)((1u << 0) | (1u << 1) | (1u << 4)) };

    const hash128_t* ids = tier_tree_id_array(t);
    ASSERT_NE(nullptr, ids);

    std::vector<hash128_t> filter_out(7);
    size_t filter_n = 0;
    ASSERT_EQ(0, merkle_dedup_filter_novel(ids, 7, bm, 7, filter_out.data(), &filter_n));

    std::vector<uint32_t> short_out(7);
    size_t short_n = 0;
    ASSERT_EQ(0, merkle_dedup_trunk_shortcircuit(t, bm, 7, short_out.data(), &short_n));

    EXPECT_EQ(filter_n, short_n);
    EXPECT_EQ(4u, short_n);
    const uint32_t expected[] = {2, 3, 5, 6};
    for (size_t i = 0; i < 4; ++i) EXPECT_EQ(expected[i], short_out[i]);
    tier_tree_free(t);
}

TEST(LaplaceCoreMerkleDedup, TrunkShortcircuitRejectsTooSmallBitmap) {
    tier_tree_t* t = sample_tree();
    uint8_t bm = 0;
    uint32_t out[7];
    size_t n = 0;
    EXPECT_NE(0, merkle_dedup_trunk_shortcircuit(t, &bm, 6, out, &n));
    tier_tree_free(t);
}
