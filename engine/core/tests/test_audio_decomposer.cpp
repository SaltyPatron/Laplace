#include <gtest/gtest.h>

#include <vector>

extern "C" {
#include "laplace/core/audio_decomposer.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/modality_atoms.h"
#include "laplace/core/modality_witness.h"
#include "laplace/core/tier_tree.h"
}

TEST(AudioDecomposer, SampleTierZeroLeavesAreDigitCodepoints) {
    /* 1234 → digit codepoints '1','2','3','4' (U+0031..U+0034). */
    int16_t pcm[1] = {1234};
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(laplace_audio_decomposer_run(pcm, 1, &tree), 0);
    ASSERT_NE(tree, nullptr);

    /* 4 digits + Sample + Window + OnsetSegment + Phrase + Track */
    EXPECT_EQ(tier_tree_node_count(tree), 9u);

    const uint32_t expect_digits[4] = {0x31u, 0x32u, 0x33u, 0x34u};
    for (uint32_t i = 0; i < 4; ++i) {
        tier_node_view_t leaf;
        ASSERT_EQ(tier_tree_get_node(tree, i, &leaf), 0);
        EXPECT_EQ(leaf.tier, 0) << "leaf " << i;
        EXPECT_EQ(leaf.atom, expect_digits[i]) << "leaf " << i;
    }

    tier_node_view_t sample;
    ASSERT_EQ(tier_tree_get_node(tree, 4, &sample), 0);
    EXPECT_EQ(sample.tier, 1);
    EXPECT_EQ(sample.first_child_idx, 0u);
    EXPECT_EQ(sample.child_count, 4u);

    tier_node_view_t window;
    ASSERT_EQ(tier_tree_get_node(tree, 5, &window), 0);
    EXPECT_EQ(window.tier, 2);
    EXPECT_EQ(window.first_child_idx, 4u);
    EXPECT_EQ(window.child_count, 1u);

    tier_node_view_t track;
    ASSERT_EQ(tier_tree_get_node(tree, 8, &track), 0);
    EXPECT_EQ(track.tier, 5);

    tier_tree_free(tree);
}

TEST(AudioDecomposer, NegativeSampleUsesMinusCodepoint) {
    int16_t pcm[1] = {-42};
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(laplace_audio_decomposer_run(pcm, 1, &tree), 0);
    ASSERT_NE(tree, nullptr);

    /* '-', '4', '2' + Sample + Window + Segment + Phrase + Track = 8 */
    EXPECT_EQ(tier_tree_node_count(tree), 8u);

    tier_node_view_t a, b, c;
    ASSERT_EQ(tier_tree_get_node(tree, 0, &a), 0);
    ASSERT_EQ(tier_tree_get_node(tree, 1, &b), 0);
    ASSERT_EQ(tier_tree_get_node(tree, 2, &c), 0);
    EXPECT_EQ(a.tier, 0);
    EXPECT_EQ(a.atom, 0x2Du);
    EXPECT_EQ(b.atom, 0x34u);
    EXPECT_EQ(c.atom, 0x32u);

    tier_node_view_t sample;
    ASSERT_EQ(tier_tree_get_node(tree, 3, &sample), 0);
    EXPECT_EQ(sample.tier, 1);
    EXPECT_EQ(sample.child_count, 3u);

    tier_tree_free(tree);
}

TEST(AudioDecomposer, ZeroSampleIsSingleDigitZero) {
    int16_t pcm[1] = {0};
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(laplace_audio_decomposer_run(pcm, 1, &tree), 0);
    tier_node_view_t leaf;
    ASSERT_EQ(tier_tree_get_node(tree, 0, &leaf), 0);
    EXPECT_EQ(leaf.tier, 0);
    EXPECT_EQ(leaf.atom, 0x30u);
    tier_node_view_t sample;
    ASSERT_EQ(tier_tree_get_node(tree, 1, &sample), 0);
    EXPECT_EQ(sample.tier, 1);
    EXPECT_EQ(sample.child_count, 1u);
    tier_tree_free(tree);
}

TEST(AudioDecomposer, ComposeDeterministicAndLengthSensitive) {
    std::vector<int16_t> a(600, 0);
    for (size_t i = 0; i < a.size(); ++i) a[i] = (int16_t)(i - 300);
    hash128_t ra, rb;
    ASSERT_EQ(laplace_audio_root_id(a.data(), a.size(), &ra), 0);
    ASSERT_EQ(laplace_audio_root_id(a.data(), a.size(), &rb), 0);
    EXPECT_EQ(hash128_compare(&ra, &rb), 0);

    std::vector<int16_t> b = a;
    b.push_back(1);
    hash128_t rc;
    ASSERT_EQ(laplace_audio_root_id(b.data(), b.size(), &rc), 0);
    EXPECT_NE(hash128_compare(&ra, &rc), 0);
}

TEST(AudioDecomposer, TypeIdsMatchCodepointFloorLadder) {
    hash128_t cp = laplace_modality_tier_type_id(LAPLACE_MODALITY_AUDIO, 0);
    hash128_t expect;
    hash128_blake3_str("Codepoint", &expect);
    EXPECT_EQ(hash128_compare(&cp, &expect), 0);

    hash128_t sample = laplace_modality_tier_type_id(LAPLACE_MODALITY_AUDIO, 1);
    hash128_blake3_str("Sample", &expect);
    EXPECT_EQ(hash128_compare(&sample, &expect), 0);

    hash128_t window = laplace_modality_tier_type_id(LAPLACE_MODALITY_AUDIO, 2);
    hash128_blake3_str("Window", &expect);
    EXPECT_EQ(hash128_compare(&window, &expect), 0);

    hash128_t track = laplace_modality_tier_type_id(LAPLACE_MODALITY_AUDIO, 5);
    hash128_blake3_str("Track", &expect);
    EXPECT_EQ(hash128_compare(&track, &expect), 0);
}

TEST(AudioDecomposer, ComposeLeafIdsMatchCodepointTable) {
    int16_t pcm[1] = {1234};
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(laplace_audio_tree_build(pcm, 1, &tree), 0);
    ASSERT_NE(tree, nullptr);

    for (uint32_t i = 0; i < 4; ++i) {
        tier_node_view_t leaf;
        ASSERT_EQ(tier_tree_get_node(tree, i, &leaf), 0);
        hash128_t expect_id;
        double coord[4];
        hilbert128_t hb;
        ASSERT_EQ(codepoint_table_resolve_atom(leaf.atom, &expect_id, coord, &hb), 0);
        EXPECT_EQ(hash128_compare(&leaf.id, &expect_id), 0) << "digit leaf " << i;
    }
    tier_tree_free(tree);
}

TEST(AudioDecomposer, ResolverUsesCodepointsNotPcmAtoms) {
    uint32_t digit_atom = 0x31u; /* '1' */
    laplace_modality_t mod = LAPLACE_MODALITY_AUDIO;
    hash128_t id_res, id_cp;
    double c1[4], c2[4];
    hilbert128_t h1, h2;
    ASSERT_EQ(laplace_modality_hash_composer_resolver(digit_atom, &mod, &id_res, c1, &h1), 0);
    ASSERT_EQ(codepoint_table_resolve_atom(digit_atom, &id_cp, c2, &h2), 0);
    EXPECT_EQ(hash128_compare(&id_res, &id_cp), 0);
}
