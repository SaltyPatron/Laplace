#include <gtest/gtest.h>

#include <cstring>
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
