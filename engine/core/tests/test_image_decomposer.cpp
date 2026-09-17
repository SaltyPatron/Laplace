#include <gtest/gtest.h>

#include <vector>
#include <memory>

extern "C" {
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/audio_decomposer.h"
#include "laplace/core/image_decomposer.h"
#include "laplace/core/modality_witness.h"
#include "laplace/core/tier_tree.h"
}

TEST(ImageDecomposer, WhitePixelOneByOneDigitLadder) {
    /* Operator invention: 255 → digit codepoints 2,5,5 per channel; RGBA white. */
    uint8_t px[4] = {255, 255, 255, 255};
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(laplace_image_decomposer_run(px, 1, 1, &tree), 0);
    ASSERT_NE(tree, nullptr);

    /* 12 digits + 4 Number + 4 Channel + 1 Pixel + 1 Patch + 1 Region + 1 Image */
    EXPECT_EQ(tier_tree_node_count(tree), 24u);

    const uint32_t expect_digits[12] = {
        (uint32_t)'2', (uint32_t)'5', (uint32_t)'5',
        (uint32_t)'2', (uint32_t)'5', (uint32_t)'5',
        (uint32_t)'2', (uint32_t)'5', (uint32_t)'5',
        (uint32_t)'2', (uint32_t)'5', (uint32_t)'5',
    };
    for (uint32_t i = 0; i < 12; ++i) {
        tier_node_view_t leaf;
        ASSERT_EQ(tier_tree_get_node(tree, i, &leaf), 0);
        EXPECT_EQ(leaf.tier, LAPLACE_IMAGE_TIER_CODEPOINT);
        EXPECT_EQ(leaf.atom, expect_digits[i]) << "digit leaf " << i;
        EXPECT_EQ(leaf.child_count, 0u);
    }

    /* Numbers: each wraps three digit leaves. */
    for (uint32_t ch = 0; ch < 4; ++ch) {
        tier_node_view_t num;
        ASSERT_EQ(tier_tree_get_node(tree, 12 + ch, &num), 0);
        EXPECT_EQ(num.tier, LAPLACE_IMAGE_TIER_NUMBER);
        EXPECT_EQ(num.first_child_idx, ch * 3u);
        EXPECT_EQ(num.child_count, 3u);
    }

    /* Channels: each wraps one Number. */
    for (uint32_t ch = 0; ch < 4; ++ch) {
        tier_node_view_t chan;
        ASSERT_EQ(tier_tree_get_node(tree, 16 + ch, &chan), 0);
        EXPECT_EQ(chan.tier, LAPLACE_IMAGE_TIER_CHANNEL);
        EXPECT_EQ(chan.first_child_idx, 12 + ch);
        EXPECT_EQ(chan.child_count, 1u);
    }

    tier_node_view_t pixel;
    ASSERT_EQ(tier_tree_get_node(tree, 20, &pixel), 0);
    EXPECT_EQ(pixel.tier, LAPLACE_IMAGE_TIER_PIXEL);
    EXPECT_EQ(pixel.first_child_idx, 16u);
    EXPECT_EQ(pixel.child_count, 4u);

    tier_node_view_t patch;
    ASSERT_EQ(tier_tree_get_node(tree, 21, &patch), 0);
    EXPECT_EQ(patch.tier, LAPLACE_IMAGE_TIER_PATCH);
    EXPECT_EQ(patch.first_child_idx, 20u);
    EXPECT_EQ(patch.child_count, 1u);

    tier_node_view_t region;
    ASSERT_EQ(tier_tree_get_node(tree, 22, &region), 0);
    EXPECT_EQ(region.tier, LAPLACE_IMAGE_TIER_REGION);

    tier_node_view_t image;
    ASSERT_EQ(tier_tree_get_node(tree, 23, &image), 0);
    EXPECT_EQ(image.tier, LAPLACE_IMAGE_TIER_IMAGE);

    tier_tree_free(tree);
}

TEST(ImageDecomposer, WhitePixelNumberIdMatchesContentRoot) {
    uint8_t px[4] = {255, 255, 255, 255};
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(laplace_image_tree_build(px, 1, 1, &tree), 0);
    ASSERT_NE(tree, nullptr);

    hash128_t expect;
    const uint8_t digits[] = {'2', '5', '5'};
    ASSERT_EQ(laplace_content_root_id(digits, 3, &expect), 0);

    for (uint32_t ch = 0; ch < 4; ++ch) {
        tier_node_view_t num;
        ASSERT_EQ(tier_tree_get_node(tree, 12 + ch, &num), 0);
        EXPECT_EQ(num.tier, LAPLACE_IMAGE_TIER_NUMBER);
        EXPECT_EQ(hash128_compare(&num.id, &expect), 0) << "channel " << ch;
    }
    tier_tree_free(tree);
}

TEST(ImageDecomposer, ComposeIsDeterministic) {
    uint8_t rgba[16] = {
        0xFF,0,0,0xFF,  0,0xFF,0,0xFF,
        0,0,0xFF,0xFF,  0x11,0x22,0x33,0x44,
    };
    hash128_t a, b;
    ASSERT_EQ(laplace_image_root_id(rgba, 2, 2, &a), 0);
    ASSERT_EQ(laplace_image_root_id(rgba, 2, 2, &b), 0);
    EXPECT_EQ(hash128_compare(&a, &b), 0);

    uint8_t swapped[16] = {
        0,0xFF,0,0xFF,  0xFF,0,0,0xFF,
        0,0,0xFF,0xFF,  0x11,0x22,0x33,0x44,
    };
    hash128_t c;
    ASSERT_EQ(laplace_image_root_id(swapped, 2, 2, &c), 0);
    EXPECT_NE(hash128_compare(&a, &c), 0);
}

TEST(ImageDecomposer, TypeIdsMatchLadderTiers) {
    hash128_t got, expect;

    got = laplace_modality_tier_type_id(LAPLACE_MODALITY_IMAGE, 0);
    hash128_blake3_str("Codepoint", &expect);
    EXPECT_EQ(hash128_compare(&got, &expect), 0);

    got = laplace_modality_tier_type_id(LAPLACE_MODALITY_IMAGE, 1);
    hash128_blake3_str("Number", &expect);
    EXPECT_EQ(hash128_compare(&got, &expect), 0);

    got = laplace_modality_tier_type_id(LAPLACE_MODALITY_IMAGE, 2);
    hash128_blake3_str("Channel", &expect);
    EXPECT_EQ(hash128_compare(&got, &expect), 0);

    got = laplace_modality_tier_type_id(LAPLACE_MODALITY_IMAGE, 3);
    hash128_blake3_str("Pixel", &expect);
    EXPECT_EQ(hash128_compare(&got, &expect), 0);

    got = laplace_modality_tier_type_id(LAPLACE_MODALITY_IMAGE, 4);
    hash128_blake3_str("Patch", &expect);
    EXPECT_EQ(hash128_compare(&got, &expect), 0);

    got = laplace_modality_tier_type_id(LAPLACE_MODALITY_IMAGE, 5);
    hash128_blake3_str("Region", &expect);
    EXPECT_EQ(hash128_compare(&got, &expect), 0);

    got = laplace_modality_tier_type_id(LAPLACE_MODALITY_IMAGE, 6);
    hash128_blake3_str("Image", &expect);
    EXPECT_EQ(hash128_compare(&got, &expect), 0);
}

TEST(ImageDecomposer, ModalityFormsSurvivePresentEntitiesAndRepeatedSourceOccurrences) {
    for (const auto modality : {LAPLACE_MODALITY_IMAGE, LAPLACE_MODALITY_AUDIO}) {
        SCOPED_TRACE((int)modality);
        tier_tree_t* raw_tree = nullptr;
        if (modality == LAPLACE_MODALITY_IMAGE) {
            const uint8_t rgba[] = {123, 123, 123, 255, 123, 123, 123, 255};
            ASSERT_EQ(0, laplace_image_tree_build(rgba, 2, 1, &raw_tree));
        } else {
            const int16_t pcm[] = {1234, 1234, -4321, 1234};
            ASSERT_EQ(0, laplace_audio_tree_build(pcm, 4, &raw_tree));
        }
        std::unique_ptr<tier_tree_t, decltype(&tier_tree_free)> tree(raw_tree, tier_tree_free);
        ASSERT_NE(nullptr, tree.get());
        std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)> full(
            intent_stage_new(64), intent_stage_free);
        std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)> known(
            intent_stage_new(64), intent_stage_free);
        ASSERT_NE(nullptr, full.get());
        ASSERT_NE(nullptr, known.get());
        hash128_t source{}, full_root{}, known_root{};
        hash128_blake3_str("modality-source-occurrence-test", &source);
        const size_t nodes = tier_tree_node_count(tree.get());
        std::vector<uint8_t> present((nodes + 7) / 8, 255);
        ASSERT_EQ(0, laplace_modality_witness_emit_tree(
            full.get(), tree.get(), modality, &source, nullptr, 0, &full_root));
        const size_t entities = intent_stage_entity_count(full.get());
        const size_t forms = intent_stage_physicality_count(full.get());
        ASSERT_GT(entities, 0u);
        ASSERT_GT(forms, entities); // repeated content retains actual raw occurrences
        ASSERT_EQ(0, laplace_modality_witness_emit_tree(
            known.get(), tree.get(), modality, &source, present.data(), nodes, &known_root));
        EXPECT_EQ(0, hash128_compare(&full_root, &known_root));
        EXPECT_EQ(0u, intent_stage_entity_count(known.get()));
        EXPECT_EQ(forms, intent_stage_physicality_count(known.get()));

        // A stage-seen root suppresses no second observed form. Entity identity
        // remains canonical while physicality source occurrence multiplicity doubles.
        ASSERT_EQ(0, laplace_modality_witness_emit_tree(
            full.get(), tree.get(), modality, &source, nullptr, 0, &full_root));
        ASSERT_EQ(0, laplace_modality_witness_emit_tree(
            known.get(), tree.get(), modality, &source, present.data(), nodes, &known_root));
        EXPECT_EQ(entities, intent_stage_entity_count(full.get()));
        EXPECT_EQ(2 * forms, intent_stage_physicality_count(full.get()));
        EXPECT_EQ(2 * forms, intent_stage_physicality_count(known.get()));

        // Compare every semantic body field and duplicate multiplicity independently
        // of the historical first-winner row ordering used by entity insertion.
        (void)intent_stage_retain_physicalities(full.get());
        hash128_t full_digest{}, known_digest{};
        ASSERT_EQ(0, intent_stage_semantic_digest(full.get(), &full_digest));
        ASSERT_EQ(0, intent_stage_semantic_digest(known.get(), &known_digest));
        EXPECT_EQ(0, hash128_compare(&full_digest, &known_digest));
    }
}
