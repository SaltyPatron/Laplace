#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/text_decomposer.h"
#include "laplace/core/tier_tree.h"

namespace {
struct TreeStats { int leaves = 0, roots = 0, compositions = 0; };
static TreeStats classify(tier_tree_t* tree) {
    TreeStats result;
    const size_t count = tier_tree_node_count(tree);
    EXPECT_EQ(0, laplace_text_decomposer_validate_roundtrip(tree));
    for (uint32_t i = 0; i < count; ++i) {
        tier_node_view_t node{};
        EXPECT_EQ(0, tier_tree_get_node(tree, i, &node));
        if (!node.tier) ++result.leaves;
        if (node.parent_idx == TIER_TREE_INVALID) ++result.roots;
        if (node.child_count > 1) ++result.compositions;
        for (uint32_t c = 0; c < node.child_count; ++c) {
            tier_node_view_t child{};
            EXPECT_EQ(0, tier_tree_get_node(tree, node.first_child_idx + c, &child));
            EXPECT_EQ(i, child.parent_idx);
            EXPECT_LT(child.tier, node.tier);
            EXPECT_GE(child.text_range_off, node.text_range_off);
            EXPECT_LE(child.text_range_off + child.text_range_len,
                      node.text_range_off + node.text_range_len);
        }
    }
    EXPECT_EQ(count ? 1 : 0, result.roots);
    return result;
}
}

TEST(LaplaceCoreTextDecomposer, EmptyInputHasNoInventedContentRoot) {
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(nullptr, 0, &tree));
    ASSERT_NE(nullptr, tree);
    EXPECT_EQ(0u, tier_tree_node_count(tree));
    EXPECT_EQ(0, classify(tree).leaves);
    tier_tree_free(tree);
}
TEST(LaplaceCoreTextDecomposer, RejectsNullOutPointer) {
    EXPECT_EQ(-1, laplace_text_decomposer_run((const uint8_t*)"hi", 2, nullptr));
}
TEST(LaplaceCoreTextDecomposer, RejectsInvalidUtf8) {
    tier_tree_t* tree = nullptr;
    const uint8_t invalid[] = {0xFF, 0xFE, 0xFD};
    EXPECT_EQ(-2, laplace_text_decomposer_run(invalid, sizeof(invalid), &tree));
    EXPECT_EQ(nullptr, tree);
}
TEST(LaplaceCoreTextDecomposer, ASCIIHelloWorldTopology) {
    tier_tree_t* tree = nullptr;
    const char* text = "Hello world.";
    ASSERT_EQ(0, laplace_text_decomposer_run((const uint8_t*)text, std::strlen(text), &tree));
    const auto stats = classify(tree);
    EXPECT_EQ(12, stats.leaves);
    EXPECT_GT(stats.compositions, 1);
    tier_tree_free(tree);
}
TEST(LaplaceCoreTextDecomposer, NormalizationIsExplicitAndOriginalScalarsAreRetained) {
    const uint8_t pre[] = {0xC3, 0xA9};
    const uint8_t dec[] = {0x65, 0xCC, 0x81};
    tier_tree_t *a = nullptr, *b = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(pre, sizeof(pre), &a));
    ASSERT_EQ(0, laplace_text_decomposer_run(dec, sizeof(dec), &b));
    EXPECT_EQ(1, classify(a).leaves);
    EXPECT_EQ(2, classify(b).leaves);
    tier_node_view_t first{}, second{};
    ASSERT_EQ(0, tier_tree_get_node(b, 0, &first));
    ASSERT_EQ(0, tier_tree_get_node(b, 1, &second));
    EXPECT_EQ(0x65u, first.atom);
    EXPECT_EQ(0x301u, second.atom);
    hash128_t nfc{}, nfd{};
    ASSERT_EQ(0, laplace_content_root_id(pre, sizeof(pre), &nfc));
    ASSERT_EQ(0, laplace_content_root_id(dec, sizeof(dec), &nfd));
    EXPECT_FALSE(hash128_equals(&nfc, &nfd));
    tier_tree_free(a); tier_tree_free(b);
}
TEST(LaplaceCoreTextDecomposer, DeterministicAcrossRuns) {
    const char* text = "The quick brown fox jumps over the lazy dog.";
    tier_tree_t *a = nullptr, *b = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run((const uint8_t*)text, std::strlen(text), &a));
    ASSERT_EQ(0, laplace_text_decomposer_run((const uint8_t*)text, std::strlen(text), &b));
    ASSERT_EQ(tier_tree_node_count(a), tier_tree_node_count(b));
    for (uint32_t i = 0; i < tier_tree_node_count(a); ++i) {
        tier_node_view_t va{}, vb{};
        ASSERT_EQ(0, tier_tree_get_node(a, i, &va));
        ASSERT_EQ(0, tier_tree_get_node(b, i, &vb));
        EXPECT_EQ(va.tier, vb.tier);
        EXPECT_EQ(va.atom, vb.atom);
        EXPECT_EQ(va.first_child_idx, vb.first_child_idx);
        EXPECT_EQ(va.child_count, vb.child_count);
        EXPECT_EQ(va.parent_idx, vb.parent_idx);
        EXPECT_TRUE(hash128_equals(&va.id, &vb.id));
        EXPECT_EQ(0, std::memcmp(va.coord, vb.coord, sizeof(va.coord)));
    }
    tier_tree_free(a); tier_tree_free(b);
}
TEST(LaplaceCoreTextDecomposer, MultiLanguageInputSegmentsCleanly) {
    const char* text = "Hello мир 中国 שלום";
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run((const uint8_t*)text, std::strlen(text), &tree));
    EXPECT_EQ(17, classify(tree).leaves);
    tier_tree_free(tree);
}
TEST(LaplaceCoreTextDecomposer, WordOccurrencesAreIndependentOfDagHeight) {
    const std::vector<std::pair<std::string, std::vector<std::string>>> cases = {
        {"What is a cat?", {"What", " ", "is", " ", "a", " ", "cat", "?"}},
        {"cat", {"cat"}},
        {"a a", {"a", " ", "a"}},
        {"cafe\xcc\x81 cat", {"cafe\xcc\x81", " ", "cat"}},
        {"One.\n\nTwo!", {"One", ".", "\n", "\n", "Two", "!"}},
    };
    for (const auto& [text, expected] : cases) {
        SCOPED_TRACE(text);
        tier_tree_t* tree = nullptr;
        ASSERT_EQ(0, laplace_text_decomposer_run((const uint8_t*)text.data(), text.size(), &tree));
        size_t count = 0;
        const uint32_t* words = tier_tree_word_segments(tree, &count);
        ASSERT_EQ(expected.size(), count);
        size_t offset = 0;
        for (size_t i = 0; i < count; i++) {
            tier_node_view_t node{};
            ASSERT_EQ(0, tier_tree_get_node(tree, words[i], &node));
            EXPECT_EQ(offset, node.text_range_off);
            EXPECT_EQ(expected[i].size(), node.text_range_len);
            EXPECT_EQ(expected[i], text.substr(node.text_range_off, node.text_range_len));
            hash128_t standalone{};
            ASSERT_EQ(0, laplace_content_root_id((const uint8_t*)expected[i].data(), expected[i].size(), &standalone));
            EXPECT_TRUE(hash128_equals(&standalone, &node.id));
            offset += node.text_range_len;
        }
        EXPECT_EQ(text.size(), offset);
        if (text == "a a") {
            EXPECT_NE(words[0], words[2]);
            EXPECT_TRUE(hash128_equals(tier_tree_id_array(tree) + words[0], tier_tree_id_array(tree) + words[2]));
        }
        tier_tree_free(tree);
    }
}
TEST(LaplaceCoreTextDecomposer, GraphemeCompositionPreservesBothScalars) {
    const uint8_t text[] = {0x65, 0xCC, 0x81};
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(text, sizeof(text), &tree));
    const auto stats = classify(tree);
    EXPECT_EQ(2, stats.leaves);
    EXPECT_EQ(1, stats.compositions);
    tier_tree_free(tree);
}
TEST(LaplaceCoreTextDecomposer, FinalizedParentIdxIsCorrect) {
    const char* text = "hello";
    tier_tree_t* tree = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run((const uint8_t*)text, std::strlen(text), &tree));
    classify(tree);
    tier_node_view_t root{};
    ASSERT_EQ(0, tier_tree_get_node(tree, (uint32_t)tier_tree_node_count(tree) - 1, &root));
    EXPECT_GT(root.tier, 0);
    EXPECT_EQ(TIER_TREE_INVALID, root.parent_idx);
    EXPECT_EQ(0u, root.text_range_off);
    EXPECT_EQ(std::strlen(text), root.text_range_len);
    tier_tree_free(tree);
}

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"

static hash128_t t0_id(uint32_t cp) {
    const codepoint_entry_t* e = codepoint_table_lookup(cp);
    EXPECT_NE(nullptr, e);
    return e->hash;
}

TEST(LaplaceContentRootId, SingleCharCollapsesToCodepointId) {
    hash128_t id;
    ASSERT_EQ(0, laplace_content_root_id((const uint8_t*)"a", 1, &id));
    hash128_t expect = t0_id('a');
    EXPECT_TRUE(hash128_equals(&id, &expect));
}

TEST(LaplaceContentRootId, AsciiWordIsFlatMerkleOverCodepointIds) {
    

    hash128_t id;
    ASSERT_EQ(0, laplace_content_root_id((const uint8_t*)"dog", 3, &id));
    hash128_t kids[3] = { t0_id('d'), t0_id('o'), t0_id('g') };
    hash128_t expect;
    hash128_merkle(2, kids, 3, &expect);
    EXPECT_TRUE(hash128_equals(&id, &expect));
}

TEST(LaplaceContentRootId, MultiCodepointGraphemeComposesNested) {
    

    
    
    
    const uint8_t s[] = {0x71, 0xCC, 0x81, 0x78};
    hash128_t id;
    ASSERT_EQ(0, laplace_content_root_id(s, sizeof(s), &id));

    hash128_t g_kids[2] = { t0_id(0x0071), t0_id(0x0301) };
    hash128_t grapheme;
    hash128_merkle(1, g_kids, 2, &grapheme);
    hash128_t w_kids[2] = { grapheme, t0_id(0x0078) };
    hash128_t nested;
    hash128_merkle(2, w_kids, 2, &nested);
    EXPECT_TRUE(hash128_equals(&id, &nested));

    hash128_t flat_kids[3] = { t0_id(0x0071), t0_id(0x0301), t0_id(0x0078) };
    hash128_t flat;
    hash128_merkle(2, flat_kids, 3, &flat);
    EXPECT_FALSE(hash128_equals(&id, &flat));
}

TEST(LaplaceContentRootId, MatchesContentWitnessBatchRoot) {
    const char* s = "whale song";
    hash128_t lookup_id;
    ASSERT_EQ(0, laplace_content_root_id(
        (const uint8_t*)s, std::strlen(s), &lookup_id));

    intent_stage_t* stage = intent_stage_new(64);
    ASSERT_NE(nullptr, stage);
    hash128_t source;
    hash128_blake3((const uint8_t*)"test/root-id-parity", 19, &source);
    hash128_t deposit_id;
    ASSERT_EQ(0, content_witness_batch_add(
        stage, (const uint8_t*)s, std::strlen(s), &source, &deposit_id));
    EXPECT_TRUE(hash128_equals(&lookup_id, &deposit_id));
    intent_stage_free(stage);
}

TEST(LaplaceContentRootId, RejectsEmptyAndNull) {
    hash128_t id;
    /* content_tree_build contract: -4 empty input, -1 null args. */
    EXPECT_EQ(-4, laplace_content_root_id((const uint8_t*)"x", 0, &id));
    EXPECT_EQ(-1, laplace_content_root_id(nullptr, 1, &id));
    EXPECT_EQ(-1, laplace_content_root_id((const uint8_t*)"x", 1, nullptr));
}

/* Grapheme-floor law (tier is a FLOOR): a single-codepoint UAX29 cluster IS
   its codepoint -- same content bytes, same blake3 id -- so the emission layer
   must never mint a separate tier-1 row for it. The in-memory tree still
   carries a uniform grapheme level (the format needs contiguous child ranges);
   the law binds what gets EMITTED, not the scaffold. */

TEST(LaplaceGraphemeFloorLaw, SingleCpClustersEmitNoTier1Rows) {
    /* "dog": three single-codepoint clusters (pass-through) + one word.
       Only the word row is emitted; the characters ARE the seeded tier-0
       codepoints and are referenced by id from the word's trajectory. */
    intent_stage_t* stage = intent_stage_new(64);
    ASSERT_NE(nullptr, stage);
    hash128_t source;
    hash128_blake3((const uint8_t*)"test/floor-law", 14, &source);
    hash128_t root;
    ASSERT_EQ(0, content_witness_batch_add(
        stage, (const uint8_t*)"dog", 3, &source, &root));
    EXPECT_EQ(1u, intent_stage_entity_count(stage));
    EXPECT_EQ(1u, intent_stage_physicality_count(stage));
    intent_stage_free(stage);
}

TEST(LaplaceGraphemeFloorLaw, MultiCpClusterStillMintsTier1) {
    /* "q" + U+0301 + "x" (no NFC composition exists for q-acute): one
       2-codepoint cluster (a REAL tier-1 composition) + one pass-through
       "x" + the word. Exactly the grapheme and the word are emitted. */
    const uint8_t s[] = {0x71, 0xCC, 0x81, 0x78};
    intent_stage_t* stage = intent_stage_new(64);
    ASSERT_NE(nullptr, stage);
    hash128_t source;
    hash128_blake3((const uint8_t*)"test/floor-law", 14, &source);
    hash128_t root;
    ASSERT_EQ(0, content_witness_batch_add(stage, s, sizeof(s), &source, &root));
    EXPECT_EQ(2u, intent_stage_entity_count(stage));
    EXPECT_EQ(2u, intent_stage_physicality_count(stage));
    intent_stage_free(stage);
}

TEST(LaplaceGraphemeFloorLaw, SingleCharReusesCodepointAndRetainsAtomicObservation) {
    /* "a" collapses to the tier-0 codepoint at every tier: no new entity or
       scaffold wrapper is emitted, and the root id remains the codepoint's id.
       Observing this root still retains its existing atomic physicality body. */
    intent_stage_t* stage = intent_stage_new(64);
    ASSERT_NE(nullptr, stage);
    hash128_t source;
    hash128_blake3((const uint8_t*)"test/floor-law", 14, &source);
    hash128_t root;
    ASSERT_EQ(0, content_witness_batch_add(
        stage, (const uint8_t*)"a", 1, &source, &root));
    EXPECT_EQ(0u, intent_stage_entity_count(stage));
    EXPECT_EQ(1u, intent_stage_physicality_count(stage));
    hash128_t expect = t0_id('a');
    EXPECT_TRUE(hash128_equals(&root, &expect));
    intent_stage_free(stage);
}

// ---- GH #1039/#1040/#1042: the offset-space, tier-nesting, and whitespace laws ----

namespace {
// Segment bytes retain the original input, including decomposed Unicode.
struct SegCollect {
    std::vector<std::string> words;
};
static void seg_collect(void* ctx, uint32_t, const uint8_t* span, uint32_t len, const hash128_t*) {
    ((SegCollect*)ctx)->words.emplace_back((const char*)span, len);
}
}

TEST(LaplaceCoreTextDecomposer, TreeOwnsOriginalText_OffsetsIndexIt) {
    const uint8_t nfd[] = { 'c','a','f','e', 0xCC, 0x81, ' ', 's','h','o','p' };
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(nfd, sizeof(nfd), &t));
    size_t text_len = 0;
    const uint8_t* text = tier_tree_text(t, &text_len);
    ASSERT_NE(nullptr, text);
    ASSERT_EQ(sizeof(nfd), text_len);
    EXPECT_EQ(0, std::memcmp(text, nfd, sizeof(nfd)));
    // Every node's range must lie inside the tree's own text.
    size_t n = tier_tree_node_count(t);
    for (size_t i = 0; i < n; ++i) {
        tier_node_view_t v;
        tier_tree_get_node(t, (uint32_t)i, &v);
        EXPECT_LE((size_t)v.text_range_off + v.text_range_len, text_len);
    }
    tier_tree_free(t);

    SegCollect got;
    ASSERT_EQ(0, laplace_content_word_segment(nfd, sizeof(nfd), seg_collect, &got));
    ASSERT_EQ(2u, got.words.size());
    EXPECT_EQ(std::string("cafe\xCC\x81"), got.words[0]);
    EXPECT_EQ(std::string("shop"), got.words[1]);
}

TEST(LaplaceCoreTextDecomposer, CompositionExclusionKeepsOriginalWordBytes) {
    const uint8_t qa[] = { 'x', ' ', 0xE0, 0xA5, 0x98, ' ', 'y' };
    SegCollect got;
    ASSERT_EQ(0, laplace_content_word_segment(qa, sizeof(qa), seg_collect, &got));
    ASSERT_EQ(3u, got.words.size());
    EXPECT_EQ(std::string("\xE0\xA5\x98"), got.words[1]);
    EXPECT_EQ(std::string("x"), got.words[0]);
    EXPECT_EQ(std::string("y"), got.words[2]);
}

TEST(LaplaceCoreTextDecomposer, ThaiSpacingMarkRetainsContiguousOwnership) {
    // Every scalar occurrence has exactly one parent, including spacing marks.
    const uint8_t thai[] = { 'A', 0xE0, 0xB8, 0xB3 };
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(thai, sizeof(thai), &t));
    auto s = classify(t);
    EXPECT_EQ(2, s.leaves);
    EXPECT_EQ(1, s.roots);
    // No node may be orphaned (parent set for every non-root after finalize).
    size_t n = tier_tree_node_count(t);
    for (size_t i = 0; i + 1 < n; ++i) {  // last node is the root
        tier_node_view_t v;
        tier_tree_get_node(t, (uint32_t)i, &v);
        EXPECT_NE(TIER_TREE_INVALID, v.parent_idx) << "orphan at " << i;
    }
    tier_tree_free(t);
}

TEST(LaplaceCoreTextDecomposer, HebrewPunctuationRetainsAllScalars) {
    // Native tailors punctuation into its own segment and retains every byte.
    const uint8_t heb[] = { 'a', '.', 0xD7, 0x90 };
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(heb, sizeof(heb), &t));
    auto s = classify(t);
    EXPECT_EQ(3, s.leaves);
    EXPECT_EQ(1, s.roots);
    tier_tree_free(t);
}

TEST(LaplaceCoreTextDecomposer, WhitespaceAndLineBreakBytesAreRetained) {
    const uint8_t ws[] = { 'a', ' ', ' ', 'b', '\r', '\n', 'c' };
    tier_tree_t* t = nullptr;
    ASSERT_EQ(0, laplace_text_decomposer_run(ws, sizeof(ws), &t));
    size_t text_len = 0;
    const uint8_t* text = tier_tree_text(t, &text_len);
    ASSERT_NE(nullptr, text);
    ASSERT_EQ(sizeof(ws), text_len);
    EXPECT_EQ(0, std::memcmp(text, ws, sizeof(ws)));
    EXPECT_EQ(7, classify(t).leaves);
    size_t n = tier_tree_node_count(t);
    // Byte coverage is intact: the root spans the whole text.
    tier_node_view_t root;
    tier_tree_get_node(t, (uint32_t)(n - 1), &root);
    EXPECT_EQ(0u, root.text_range_off);
    EXPECT_EQ(text_len, (size_t)root.text_range_len);
    tier_tree_free(t);
}
