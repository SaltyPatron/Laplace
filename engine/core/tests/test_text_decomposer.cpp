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

} // namespace

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
    EXPECT_EQ(12, stats.leaves);     // 12 ASCII codepoints
    EXPECT_EQ(12, stats.graphemes);   // each ASCII char its own grapheme
    EXPECT_EQ(1, stats.docs);
    EXPECT_GE(stats.words, 1);
    EXPECT_GE(stats.sentences, 1);
    tier_tree_free(t);
}

TEST(LaplaceCoreTextDecomposer, NFCEquivalentInputsProduceIdenticalTree) {
    // Same character expressed two ways:
    //   precomposed: U+00E9 (é) — one codepoint
    //   decomposed:  U+0065 U+0301 (e + combining acute) — two codepoints
    // After NFC normalization both become U+00E9.
    tier_tree_t* a = nullptr;
    tier_tree_t* b = nullptr;
    const uint8_t pre[] = {0xC3, 0xA9};                 // U+00E9 in UTF-8
    const uint8_t dec[] = {0x65, 0xCC, 0x81};            // U+0065 U+0301 in UTF-8
    ASSERT_EQ(0, laplace_text_decomposer_run(pre, sizeof(pre), &a));
    ASSERT_EQ(0, laplace_text_decomposer_run(dec, sizeof(dec), &b));
    ASSERT_EQ(tier_tree_node_count(a), tier_tree_node_count(b));
    for (size_t i = 0; i < tier_tree_node_count(a); ++i) {
        tier_node_view_t va, vb;
        tier_tree_get_node(a, (uint32_t)i, &va);
        tier_tree_get_node(b, (uint32_t)i, &vb);
        EXPECT_EQ(va.tier, vb.tier) << "idx=" << i;
        EXPECT_EQ(va.atom, vb.atom) << "idx=" << i;
        EXPECT_EQ(va.child_count, vb.child_count) << "idx=" << i;
    }
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
    // Mixed Latin + Cyrillic + Han + Hebrew. Just verify it succeeds +
    // node counts are reasonable.
    const char* s = "Hello мир 中国 שלום";
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(
        (const uint8_t*)s, std::strlen(s), &t));
    auto stats = classify(t);
    EXPECT_GT(stats.leaves, 10);
    EXPECT_EQ(1, stats.docs);
    EXPECT_GE(stats.words, 4); // 4 distinct word runs
    tier_tree_free(t);
}

TEST(LaplaceCoreTextDecomposer, GraphemeCountMatchesUserPerceivedChars) {
    // "é" as precomposed = 1 grapheme; as decomposed (e + combining
    // acute) = also 1 grapheme (combining mark joined with base).
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
    // Root has no parent
    uint32_t last = (uint32_t)(tier_tree_node_count(t) - 1);
    tier_node_view_t v;
    tier_tree_get_node(t, last, &v);
    EXPECT_EQ(4, v.tier);
    EXPECT_EQ(TIER_TREE_INVALID, v.parent_idx);
    // Leaf has parent (some grapheme)
    tier_tree_get_node(t, 0, &v);
    EXPECT_EQ(0, v.tier);
    EXPECT_NE(TIER_TREE_INVALID, v.parent_idx);
    tier_tree_free(t);
}
