#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "laplace/core/text_decomposer.h"
#include "laplace/core/tier_tree.h"

namespace {

struct TreeStats {
    int leaves = 0;
    int graphemes = 0;
    int words = 0;
    int sentences = 0;
    int docs = 0;
};

static TreeStats classify(tier_tree_t* t) {
    TreeStats s;
    size_t n = tier_tree_node_count(t);
    for (size_t i = 0; i < n; ++i) {
        tier_node_view_t v;
        tier_tree_get_node(t, (uint32_t)i, &v);
        switch (v.tier) {
            case 0: s.leaves++; break;
            case 1: s.graphemes++; break;
            case 2: s.words++; break;
            case 3: s.sentences++; break;
            case 4: s.docs++; break;
        }
    }
    return s;
}

}

TEST(LaplaceCoreTextDecomposer, EmptyInputProducesRootOnly) {
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(nullptr, 0, &t));
    ASSERT_NE(nullptr, t);
    EXPECT_EQ(1u, tier_tree_node_count(t));
    auto s = classify(t);
    EXPECT_EQ(0, s.leaves);
    EXPECT_EQ(1, s.docs);
    tier_tree_free(t);
}

TEST(LaplaceCoreTextDecomposer, RejectsNullOutPointer) {
    EXPECT_EQ(-1, laplace_text_decomposer_run((const uint8_t*)"hi", 2, nullptr));
}

TEST(LaplaceCoreTextDecomposer, RejectsInvalidUtf8) {
    tier_tree_t* t = nullptr;
    uint8_t bad[] = {0xFF, 0xFE, 0xFD};
    EXPECT_EQ(-2, laplace_text_decomposer_run(bad, sizeof(bad), &t));
    EXPECT_EQ(nullptr, t);
}

TEST(LaplaceCoreTextDecomposer, ASCIIHelloWorldTopology) {
    tier_tree_t* t = nullptr;
    const char* s = "Hello world.";
    ASSERT_EQ(0, laplace_text_decomposer_run(
        (const uint8_t*)s, std::strlen(s), &t));
    auto stats = classify(t);
    EXPECT_EQ(12, stats.leaves);
    EXPECT_EQ(12, stats.graphemes);
    EXPECT_EQ(1, stats.docs);
    EXPECT_GE(stats.words, 1);
    EXPECT_GE(stats.sentences, 1);
    tier_tree_free(t);
}

TEST(LaplaceCoreTextDecomposer, DistinctNormalizationFormsStayDistinct) {
    tier_tree_t* a = nullptr;
    tier_tree_t* b = nullptr;
    const uint8_t pre[] = {0xC3, 0xA9};
    const uint8_t dec[] = {0x65, 0xCC, 0x81};
    ASSERT_EQ(0, laplace_text_decomposer_run(pre, sizeof(pre), &a));
    ASSERT_EQ(0, laplace_text_decomposer_run(dec, sizeof(dec), &b));
    EXPECT_NE(tier_tree_node_count(a), tier_tree_node_count(b));
    tier_node_view_t la, lb;
    tier_tree_get_node(a, 0, &la);
    tier_tree_get_node(b, 0, &lb);
    EXPECT_EQ(0u, la.tier); EXPECT_EQ(0x00E9u, la.atom);
    EXPECT_EQ(0u, lb.tier); EXPECT_EQ(0x0065u, lb.atom);
    tier_tree_free(a);
    tier_tree_free(b);
}

TEST(LaplaceCoreTextDecomposer, DeterministicAcrossRuns) {
    const char* s = "The quick brown fox jumps over the lazy dog.";
    tier_tree_t* a = nullptr;
    tier_tree_t* b = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(
        (const uint8_t*)s, std::strlen(s), &a));
    ASSERT_EQ(0, laplace_text_decomposer_run(
        (const uint8_t*)s, std::strlen(s), &b));
    ASSERT_EQ(tier_tree_node_count(a), tier_tree_node_count(b));
    for (size_t i = 0; i < tier_tree_node_count(a); ++i) {
        tier_node_view_t va, vb;
        tier_tree_get_node(a, (uint32_t)i, &va);
        tier_tree_get_node(b, (uint32_t)i, &vb);
        EXPECT_EQ(va.tier, vb.tier);
        EXPECT_EQ(va.atom, vb.atom);
        EXPECT_EQ(va.first_child_idx, vb.first_child_idx);
        EXPECT_EQ(va.child_count, vb.child_count);
        EXPECT_EQ(va.parent_idx, vb.parent_idx);
    }
    tier_tree_free(a);
    tier_tree_free(b);
}

TEST(LaplaceCoreTextDecomposer, MultiLanguageInputSegmentsCleanly) {
    const char* s = "Hello мир 中国 שלום";
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(
        (const uint8_t*)s, std::strlen(s), &t));
    auto stats = classify(t);
    EXPECT_GT(stats.leaves, 10);
    EXPECT_EQ(1, stats.docs);
    EXPECT_GE(stats.words, 4);
    tier_tree_free(t);
}

TEST(LaplaceCoreTextDecomposer, GraphemeCountMatchesUserPerceivedChars) {
    tier_tree_t* a = nullptr;
    const uint8_t dec[] = {0x65, 0xCC, 0x81};
    ASSERT_EQ(0, laplace_text_decomposer_run(dec, sizeof(dec), &a));
    auto sa = classify(a);
    EXPECT_EQ(1, sa.graphemes) << "decomposed é should be one grapheme cluster";
    tier_tree_free(a);
}

TEST(LaplaceCoreTextDecomposer, FinalizedParentIdxIsCorrect) {
    const char* s = "hello";
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(
        (const uint8_t*)s, std::strlen(s), &t));
    uint32_t last = (uint32_t)(tier_tree_node_count(t) - 1);
    tier_node_view_t v;
    tier_tree_get_node(t, last, &v);
    EXPECT_EQ(4, v.tier);
    EXPECT_EQ(TIER_TREE_INVALID, v.parent_idx);
    tier_tree_get_node(t, 0, &v);
    EXPECT_EQ(0, v.tier);
    EXPECT_NE(TIER_TREE_INVALID, v.parent_idx);
    tier_tree_free(t);
}
