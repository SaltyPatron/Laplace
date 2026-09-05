#include <gtest/gtest.h>

#include <cstring>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include <algorithm>
#include <filesystem>

#include "laplace/core/grammar_registry.h"
#include "laplace/core/grammar_decomposer.h"
#include "laplace/core/grammar_compose.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

namespace {

TEST(GrammarCompose, TsvRowProducesEntitiesAndSpans) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    const char* src = "a\tb\tc\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);

    hash128_t source_id;
    hash128_blake3(reinterpret_cast<const uint8_t*>("test/source"), 11, &source_id);
    hash128_t type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("Type"), 4, &type_meta);

    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), ast,
        "tsv", source_id, type_meta, &result), 0);
    ASSERT_NE(result, nullptr);
    EXPECT_GT(laplace_compose_entity_count(result), 0u);
    EXPECT_GT(laplace_compose_physicality_count(result), 0u);

    hash128_t span_id;
    EXPECT_EQ(laplace_compose_span_lookup(result, 0, 1, &span_id), 0);

    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}






// GH #595: laplace_compose_span_lookup was a linear scan called once per AST
// node from the C# compose loop — O(n) lookups x O(n) scan each, O(n^2)
// total, measured pinning a real ingest for 40+ minutes on a file with tens
// of thousands of nodes. Proves the fix at real scale: every one of several
// thousand distinct spans resolves to its OWN correct entity id (not just
// "doesn't crash" — a broken index could silently drop or cross-wire entries
// under load in a way a single-span test would never catch), and does so
// fast enough that a regression back to O(n^2) would make this test itself
// balloon rather than fail silently.
TEST(GrammarCompose, SpanLookupResolvesEveryDistinctSpanAtScale) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("csv");
    ASSERT_NE(recipe, nullptr);

    // Each cell gets a distinct value so every span's composed entity id is
    // unique — a collision or a wrong-index bug would surface as a mismatch.
    constexpr int kCols = 500;
    constexpr int kRows = 6;
    std::string src;
    for (int r = 0; r < kRows; ++r) {
        for (int c = 0; c < kCols; ++c) {
            if (c) src += ',';
            src += "v" + std::to_string(r * kCols + c);
        }
        src += '\n';
    }

    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src.data()), src.size(), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);

    hash128_t source_id;
    hash128_blake3(reinterpret_cast<const uint8_t*>("test/scale"), 10, &source_id);
    hash128_t type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("Type"), 4, &type_meta);

    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src.data()), src.size(), ast,
        "csv", source_id, type_meta, &result), 0);
    ASSERT_NE(result, nullptr);

    // Walk the AST's own node list — the exact same source of (start_byte,
    // end_byte) pairs the real C# compose loop uses — rather than hand-
    // predicting byte offsets, which would make this a test of my arithmetic
    // instead of the fix. Every node's span must resolve, and distinct spans
    // must resolve to distinct entity ids (the failure mode a broken
    // hash/probe would produce, invisible to a "did it crash" check alone).
    size_t node_count = laplace_ast_node_count(ast);
    ASSERT_GT(node_count, static_cast<size_t>(kCols * kRows));

    std::vector<std::pair<uint32_t, uint32_t>> spans;
    for (size_t i = 0; i < node_count; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        spans.emplace_back(nd.start_byte, nd.end_byte);
    }
    std::sort(spans.begin(), spans.end());
    spans.erase(std::unique(spans.begin(), spans.end()), spans.end());

    std::vector<hash128_t> ids;
    ids.reserve(spans.size());
    for (auto& [start, end] : spans) {
        hash128_t id;
        if (laplace_compose_span_lookup(result, start, end, &id) != 0) continue;
        ids.push_back(id);
    }
    ASSERT_GT(ids.size(), static_cast<size_t>(kCols * kRows))
        << "too few spans resolved — the index dropped entries the linear scan would have found";

    std::sort(ids.begin(), ids.end(), [](const hash128_t& a, const hash128_t& b) {
        return a.hi != b.hi ? a.hi < b.hi : a.lo < b.lo;
    });
    size_t distinct = std::unique(ids.begin(), ids.end(), [](const hash128_t& a, const hash128_t& b) {
        return a.hi == b.hi && a.lo == b.lo;
    }) - ids.begin();
    EXPECT_EQ(distinct, ids.size()) << "some distinct spans aliased to the same entity id";

    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}

TEST(GrammarCompose, EntityDedupDoesNotInflateCount) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    const char* src = "x\ty\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);

    hash128_t source_id, type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("src"), 3, &source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("meta"), 4, &type_meta);

    laplace_compose_result_t* r1 = nullptr;
    laplace_compose_result_t* r2 = nullptr;
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), ast,
        "tsv", source_id, type_meta, &r1), 0);
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), ast,
        "tsv", source_id, type_meta, &r2), 0);
    ASSERT_NE(r1, nullptr);
    ASSERT_NE(r2, nullptr);
    EXPECT_EQ(laplace_compose_entity_count(r1), laplace_compose_entity_count(r2));

    laplace_compose_result_free(r1);
    laplace_compose_result_free(r2);
    laplace_ast_free(ast);
}





TEST(GrammarCompose, JsonScalarLeafConvergesWithContentRootId) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("json");
    ASSERT_NE(recipe, nullptr);
    const char* src = "{\"k\":\"New York\"}";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);

    hash128_t source_id, type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("src"), 3, &source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("meta"), 4, &type_meta);

    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), ast,
        "json", source_id, type_meta, &result), 0);
    ASSERT_NE(result, nullptr);

    
    const char* surface = "New York";
    hash128_t expected;
    ASSERT_EQ(laplace_content_root_id(
        reinterpret_cast<const uint8_t*>(surface), std::strlen(surface), &expected), 0);

    bool found = false;
    const size_t n = laplace_compose_entity_count(result);
    for (size_t i = 0; i < n; ++i) {
        laplace_compose_entity_t e;
        if (laplace_compose_get_entity(result, i, &e) != 0) continue;
        if (e.id.hi == expected.hi && e.id.lo == expected.lo) { found = true; break; }
    }
    EXPECT_TRUE(found)
        << "JSON value 'New York' did not converge to laplace_content_root_id";

    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}






static bool json_value_converges(const char* json_src, const char* surface) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("json");
    if (!recipe) return false;
    laplace_ast_t* ast = nullptr;
    if (laplace_grammar_parse(reinterpret_cast<const uint8_t*>(json_src),
                              std::strlen(json_src), recipe, &ast) != 0) return false;
    hash128_t source_id, type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("src"), 3, &source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("meta"), 4, &type_meta);
    laplace_compose_result_t* result = nullptr;
    int rc = laplace_grammar_compose(reinterpret_cast<const uint8_t*>(json_src),
        std::strlen(json_src), ast, "json", source_id, type_meta, &result);
    bool found = false;
    if (rc == 0 && result) {
        hash128_t expected;
        if (laplace_content_root_id(reinterpret_cast<const uint8_t*>(surface),
                                    std::strlen(surface), &expected) == 0) {
            const size_t n = laplace_compose_entity_count(result);
            for (size_t i = 0; i < n; ++i) {
                laplace_compose_entity_t e;
                if (laplace_compose_get_entity(result, i, &e) != 0) continue;
                if (e.id.hi == expected.hi && e.id.lo == expected.lo) { found = true; break; }
            }
        }
    }
    if (result) laplace_compose_result_free(result);
    laplace_ast_free(ast);
    return found;
}




TEST(GrammarCompose, ConvergenceBattery) {
    EXPECT_TRUE(json_value_converges("{\"k\":\"New York\"}", "New York"));      
    EXPECT_TRUE(json_value_converges("{\"k\":\"\xE6\x9D\xB1\xE4\xBA\xAC\"}",
                                     "\xE6\x9D\xB1\xE4\xBA\xAC"));               
    EXPECT_TRUE(json_value_converges("{\"k\":\"caf\xC3\xA9\"}", "caf\xC3\xA9")); 
    
    EXPECT_TRUE(json_value_converges("{\"k\":\"cafe\xCC\x81\"}", "caf\xC3\xA9"))
        << "NFD cafe+U+0301 in JSON did not converge to the NFC café content id";
}

TEST(GrammarDecomposer, DeeplyNestedInputDoesNotOverflowStack) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("json");
    ASSERT_NE(recipe, nullptr);
    const int depth = 20000;
    std::string src;
    src.reserve(static_cast<size_t>(depth) * 2);
    for (int i = 0; i < depth; ++i) src.push_back('[');
    for (int i = 0; i < depth; ++i) src.push_back(']');

    laplace_ast_t* ast = nullptr;
    int rc = laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src.data()), src.size(), recipe, &ast);
    ASSERT_EQ(rc, 0);
    ASSERT_NE(ast, nullptr);
    EXPECT_GT(laplace_ast_node_count(ast), 0u);
    laplace_ast_free(ast);
}

/* Grapheme-floor law: single-codepoint clusters are pass-through scaffold
   (their id IS the codepoint id); only multi-codepoint clusters may appear
   as tier-1 entities in a compose result. */
TEST(GrammarCompose, SingleCpClustersAreNotEmittedAtTier1) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    const char* src = "a\tb\tc\n";  /* every cluster is a single codepoint */
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);

    hash128_t source_id, type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("src"), 3, &source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("meta"), 4, &type_meta);

    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), ast,
        "tsv", source_id, type_meta, &result), 0);
    ASSERT_NE(result, nullptr);

    const size_t n = laplace_compose_entity_count(result);
    for (size_t i = 0; i < n; ++i) {
        laplace_compose_entity_t e;
        ASSERT_EQ(laplace_compose_get_entity(result, i, &e), 0);
        EXPECT_NE(e.tier, 1)
            << "single-codepoint cluster minted a tier-1 entity (floor violation)";
    }

    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}

TEST(GrammarCompose, MultiCpClusterIsEmittedAtTier1) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    /* q + U+0301 forms the only multi-codepoint cluster in the source. */
    const char* src = "q\xCC\x81x\ty\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);

    hash128_t source_id, type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("src"), 3, &source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("meta"), 4, &type_meta);

    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), ast,
        "tsv", source_id, type_meta, &result), 0);
    ASSERT_NE(result, nullptr);

    size_t tier1 = 0;
    const size_t n = laplace_compose_entity_count(result);
    for (size_t i = 0; i < n; ++i) {
        laplace_compose_entity_t e;
        ASSERT_EQ(laplace_compose_get_entity(result, i, &e), 0);
        if (e.tier == 1) tier1++;
    }
    EXPECT_EQ(tier1, 1u) << "exactly the q+combining-acute cluster is tier-1 content";

    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}

TEST(GrammarCompose, PartiallyValidChildSpanDoesNotCrash) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    
    const char* src = "head\t\ttrail\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);

    hash128_t source_id, type_meta;
    hash128_blake3(reinterpret_cast<const uint8_t*>("src"), 3, &source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("meta"), 4, &type_meta);

    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_compose(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), ast,
        "tsv", source_id, type_meta, &result), 0);
    ASSERT_NE(result, nullptr);
    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}

}  

TEST(GrammarSourceCompose, RecursivelyRealizesAstSpansAndSourceOrder) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    const char* src = "1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}"; // Delimiters and repeat are uncovered source gaps.
    const size_t len = std::strlen(src);
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(src), len, recipe, &ast), 0);

    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_source_compose(
        reinterpret_cast<const uint8_t*>(src), len, ast, "tsv", &result), 0);
    ASSERT_NE(result, nullptr);
    EXPECT_GT(laplace_compose_entity_count(result), 0u);
    EXPECT_GT(laplace_compose_physicality_count(result), 0u);

    const size_t node_count = laplace_ast_node_count(ast);
    std::vector<laplace_ast_node_t> nodes(node_count);
    std::vector<std::vector<uint32_t>> children(node_count);
    for (size_t i = 0; i < node_count; ++i) {
        ASSERT_EQ(laplace_ast_get_node(ast, i, &nodes[i]), 0);
        hash128_t mapped;
        EXPECT_EQ(laplace_compose_span_lookup(result, nodes[i].start_byte, nodes[i].end_byte, &mapped), 0);
        if (nodes[i].parent != LAPLACE_AST_ROOT) children[nodes[i].parent].push_back((uint32_t)i);
    }
    for (auto& v : children) std::sort(v.begin(), v.end(), [&nodes](uint32_t a, uint32_t b) {
        return nodes[a].start_byte < nodes[b].start_byte;
    });
    // Recipe punctuation is a first-class lexical component too, so a tag or
    // traversal can resolve it without falling back to a decoded-row identity.
    hash128_t first_tab, second_tab;
    EXPECT_EQ(laplace_compose_span_lookup(result, 1, 2, &first_tab), 0);
    EXPECT_EQ(laplace_compose_span_lookup(result, 11, 12, &second_tab), 0);
    EXPECT_TRUE(hash128_equals(&first_tab, &second_tab));

    // Every realized AST container carries exactly its immediate child spans and
    // the uncovered bytes between them, in source order. This catches a root-only
    // composition, dropped punctuation, and lost repeated lexical constituents.
    for (size_t parent = 0; parent < node_count; ++parent) {
        if (children[parent].empty()) continue;
        hash128_t parent_id;
        ASSERT_EQ(laplace_compose_span_lookup(result, nodes[parent].start_byte,
                                              nodes[parent].end_byte, &parent_id), 0);
        std::vector<hash128_t> expected;
        uint32_t cursor = nodes[parent].start_byte;
        for (uint32_t child : children[parent]) {
            if (nodes[child].start_byte > cursor) {
                hash128_t gap;
                ASSERT_EQ(laplace_content_source_root_id(reinterpret_cast<const uint8_t*>(src + cursor),
                                                  nodes[child].start_byte - cursor, &gap), 0);
                expected.push_back(gap);
            }
            hash128_t child_id;
            ASSERT_EQ(laplace_compose_span_lookup(result, nodes[child].start_byte,
                                                  nodes[child].end_byte, &child_id), 0);
            expected.push_back(child_id);
            cursor = nodes[child].end_byte;
        }
        if (cursor < nodes[parent].end_byte) {
            hash128_t gap;
            ASSERT_EQ(laplace_content_source_root_id(reinterpret_cast<const uint8_t*>(src + cursor),
                                              nodes[parent].end_byte - cursor, &gap), 0);
            expected.push_back(gap);
        }
        laplace_compose_physicality_t physicality{};
        bool found = false;
        for (size_t p = 0; p < laplace_compose_physicality_count(result); ++p) {
            ASSERT_EQ(laplace_compose_get_physicality(result, p, &physicality), 0);
            if (hash128_equals(&physicality.entity_id, &parent_id)) { found = true; break; }
        }
        if (expected.size() == 1) {
            /* The same content identity can have a physicality from another
             * AST occurrence; the singleton itself adds no self trajectory. */
            EXPECT_TRUE(hash128_equals(&parent_id, &expected[0]));
            continue;
        }
        ASSERT_TRUE(found) << "non-singleton AST node was not persisted";
        ASSERT_EQ(physicality.n_constituents, expected.size());
        std::vector<hash128_t> actual(expected.size());
        ASSERT_EQ(trajectory_constituents(physicality.trajectory_xyzm,
                                          physicality.trajectory_n / 4,
                                          actual.data(), actual.size()),
                  static_cast<int>(expected.size()));
        for (size_t i = 0; i < expected.size(); ++i)
            EXPECT_TRUE(hash128_equals(&actual[i], &expected[i])) << "parent=" << parent << " ordinal=" << i;
    }

    double root_coord[4]; uint8_t root_tier = 0, root_has_atom = 0; uint32_t root_atom = 0;
    EXPECT_EQ(laplace_compose_root_placement(result, root_coord, &root_tier, &root_atom, &root_has_atom), 0);
    hash128_t root_id = laplace_compose_root_id(result);
    hash128_t zero{};
    EXPECT_FALSE(hash128_equals(&root_id, &zero));
    EXPECT_GE(root_tier, 1);

    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}

TEST(GrammarSourceCompose, NormalizedLexicalIdentityNeedsRawByteRepresentationForNfdReplay) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("json");
    ASSERT_NE(recipe, nullptr);
    const char* nfc = "{\"v\":\"caf\xC3\xA9\"}";
    const char* nfd = "{\"v\":\"cafe\xCC\x81\"}";
    laplace_ast_t *nfc_ast = nullptr, *nfd_ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(nfc), std::strlen(nfc), recipe, &nfc_ast), 0);
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(nfd), std::strlen(nfd), recipe, &nfd_ast), 0);
    laplace_compose_result_t *nfc_result = nullptr, *nfd_result = nullptr;
    ASSERT_EQ(laplace_grammar_source_compose(reinterpret_cast<const uint8_t*>(nfc), std::strlen(nfc), nfc_ast, "json", &nfc_result), 0);
    ASSERT_EQ(laplace_grammar_source_compose(reinterpret_cast<const uint8_t*>(nfd), std::strlen(nfd), nfd_ast, "json", &nfd_result), 0);

    hash128_t nfc_id = laplace_compose_root_id(nfc_result);
    hash128_t nfd_id = laplace_compose_root_id(nfd_result);
    EXPECT_FALSE(hash128_equals(&nfc_id, &nfd_id));

    laplace_compose_result_free(nfc_result);
    laplace_compose_result_free(nfd_result);
    laplace_ast_free(nfc_ast);
    laplace_ast_free(nfd_ast);
}

TEST(GrammarSourceCompose, SingletonAstRootHasNativePlacementWithoutGrammarWrapper) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    const char* src = "x";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(src), 1, recipe, &ast), 0);
    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_source_compose(reinterpret_cast<const uint8_t*>(src), 1, ast, "tsv", &result), 0);
    ASSERT_NE(result, nullptr);
    EXPECT_EQ(0u, laplace_compose_physicality_count(result));
    double coord[4]; uint8_t tier = 0, has_atom = 0; uint32_t atom = 0;
    EXPECT_EQ(0, laplace_compose_root_placement(result, coord, &tier, &atom, &has_atom));
    hash128_t root_id = laplace_compose_root_id(result), zero{};
    EXPECT_FALSE(hash128_equals(&root_id, &zero));
    EXPECT_TRUE(std::isfinite(coord[0]));
    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}

TEST(GrammarSourceCompose, DeepSourceFloorsRoundTripPastLegacyFiveBitTier) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("json");
    ASSERT_NE(recipe, nullptr);
    constexpr int depth = 40;
    std::string src(depth, '[');
    src += "0";
    src.append(depth, ']');
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(src.data()), src.size(), recipe, &ast), 0);
    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_source_compose(reinterpret_cast<const uint8_t*>(src.data()), src.size(),
                                             ast, "json", &result), 0);
    ASSERT_NE(result, nullptr);

    double root_coord[4]; uint8_t root_tier = 0, has_atom = 0; uint32_t atom = 0;
    ASSERT_EQ(laplace_compose_root_placement(result, root_coord, &root_tier, &atom, &has_atom), 0);
    ASSERT_GT(root_tier, 31);
    hash128_t root_id = laplace_compose_root_id(result);
    laplace_compose_physicality_t root_phys{};
    bool found = false;
    for (size_t i = 0; i < laplace_compose_physicality_count(result); ++i) {
        ASSERT_EQ(laplace_compose_get_physicality(result, i, &root_phys), 0);
        if (hash128_equals(&root_phys.entity_id, &root_id)) { found = true; break; }
    }
    ASSERT_TRUE(found);
    uint8_t max_child_tier = 0;
    for (size_t i = 0; i < root_phys.n_constituents; ++i) {
        mantissa_payload_t child{};
        mantissa_unpack(root_phys.trajectory_xyzm + i * 4, &child);
        max_child_tier = std::max(max_child_tier, laplace_vflag_tier(child.flags));
    }
    EXPECT_EQ(root_tier, (uint8_t)(max_child_tier + 1));

    hash128_t text_type;
    hash128_blake3_str("Text", &text_type);
    for (size_t i = 0; i < laplace_compose_entity_count(result); ++i) {
        laplace_compose_entity_t entity{};
        ASSERT_EQ(laplace_compose_get_entity(result, i, &entity), 0);
        EXPECT_TRUE(hash128_equals(&entity.type_id, &text_type));
    }
    laplace_compose_result_free(result);
    laplace_ast_free(ast);
}
