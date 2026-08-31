#include <gtest/gtest.h>

#include <cstdlib>
#include <cstring>

#include "laplace/core/text_decomposer.h"
#include "laplace/core/tier_tree.h"

TEST(LaplaceTextRoundtripGate, AcceptsCanonicalNfcExpansion) {
    // U+0958 is a composition exclusion: NFC expands the 3-byte scalar to
    // U+0915 U+093C (6 bytes). The gate reconstructs those tier-0 atoms and
    // must compare against the tree-owned post-NFC buffer, not the caller's
    // shorter input allocation (#1039).
    const uint8_t input[] = { 'x', 0xE0, 0xA5, 0x98, 'y' };
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(input, sizeof(input), &tree));
    ASSERT_NE(nullptr, tree);

    size_t text_len = 0;
    const uint8_t* text = tier_tree_text(tree, &text_len);
    ASSERT_NE(nullptr, text);
    ASSERT_EQ(8u, text_len);
    EXPECT_EQ(0, laplace_text_decomposer_validate_roundtrip(tree));

    const uint8_t expected[] = {
        'x', 0xE0, 0xA4, 0x95, 0xE0, 0xA4, 0xBC, 'y'
    };
    EXPECT_EQ(0, std::memcmp(text, expected, sizeof(expected)));
    tier_tree_free(tree);
}

TEST(LaplaceTextRoundtripGate, RejectsAtomTextMismatch) {
    // Build the exact corruption class the production gate is meant to stop:
    // the tree claims tier-0 atom 'a', while its owned canonical byte is 'b'.
    // Before the final round-trip gate, such a drift can look structurally valid
    // and proceed into hashing/deposition with a content id that does not render
    // back to the bytes the tree says it represents.
    tier_tree_t* tree = tier_tree_new(2);
    ASSERT_NE(nullptr, tree);

    auto* text = static_cast<uint8_t*>(std::malloc(1));
    ASSERT_NE(nullptr, text);
    text[0] = 'b';
    ASSERT_EQ(0, tier_tree_set_text(tree, text, 1));

    uint32_t leaf = tier_tree_add_leaf(tree, 0, 'a', 0, 1);
    ASSERT_NE(TIER_TREE_INVALID, leaf);
    uint32_t root = tier_tree_add_node(tree, 4, leaf, 1, 0, 1);
    ASSERT_NE(TIER_TREE_INVALID, root);
    ASSERT_EQ(0, tier_tree_finalize(tree));

    EXPECT_EQ(-2, laplace_text_decomposer_validate_roundtrip(tree));
    tier_tree_free(tree);
}

TEST(LaplaceTextRoundtripGate, RejectsNonContiguousAtomRanges) {
    // Equal bytes are not enough: offsets must form one exact, gap-free byte
    // stream. This catches a future decomposer regression that reorders or
    // overlaps valid atoms without necessarily producing invalid UTF-8.
    tier_tree_t* tree = tier_tree_new(3);
    ASSERT_NE(nullptr, tree);

    auto* text = static_cast<uint8_t*>(std::malloc(2));
    ASSERT_NE(nullptr, text);
    text[0] = 'a';
    text[1] = 'b';
    ASSERT_EQ(0, tier_tree_set_text(tree, text, 2));

    uint32_t a = tier_tree_add_leaf(tree, 0, 'a', 0, 1);
    uint32_t b = tier_tree_add_leaf(tree, 0, 'b', 0, 1); // overlap: must be offset 1
    ASSERT_NE(TIER_TREE_INVALID, a);
    ASSERT_NE(TIER_TREE_INVALID, b);
    uint32_t root = tier_tree_add_node(tree, 4, a, 2, 0, 2);
    ASSERT_NE(TIER_TREE_INVALID, root);
    ASSERT_EQ(0, tier_tree_finalize(tree));

    EXPECT_EQ(-2, laplace_text_decomposer_validate_roundtrip(tree));
    tier_tree_free(tree);
}
