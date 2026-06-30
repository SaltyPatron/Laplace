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
#include "laplace/core/etl_ingest.h"
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

// ASAN repro for the native compose AV on multilingual free-text (OMW/Wiktionary/Tatoeba).
// Replays the exact .NET ingest path NATIVELY: the tsv row-iterator frames rows, each row is
// parsed + composed via laplace_grammar_compose. Under -fsanitize=address this catches the
// heap overrun at its file:line, instead of the .NET runtime swallowing the 0xC0000005 as a
// bare "Fatal error". Gated on LAPLACE_OMW_REPRO_DIR so the normal suite is unaffected.
TEST(GrammarCompose, OmwRowsAsanRepro) {
    const char* dir = std::getenv("LAPLACE_OMW_REPRO_DIR");
    if (!dir || !*dir) GTEST_SKIP() << "set LAPLACE_OMW_REPRO_DIR to run";

    // Drive the FULL native ETL pipeline (frame -> parse -> compose -> drain) into ONE shared
    // intent_stage that accumulates across rows/files — the batch accumulation the compose-and-free
    // repro never stressed. OMW crashes filling the first batch (bulk-fresh: no probe), so feed the
    // files in order with no existence probe / no accept filter and let it accumulate.
    laplace_etl_config_t cfg;
    std::memset(&cfg, 0, sizeof(cfg));
    cfg.modality_id = "tsv";
    hash128_blake3(reinterpret_cast<const uint8_t*>("omw/source"), 10, &cfg.source_id);
    hash128_blake3(reinterpret_cast<const uint8_t*>("Type"), 4, &cfg.type_meta_id);
    cfg.witness_weight    = 1.0;
    cfg.trust_weight      = 1.0;
    cfg.now_unix_us       = 0;
    cfg.witness_kind      = 0;   // compose+drain only (the shared suspect path)
    cfg.edge_rules        = nullptr;
    cfg.edge_rule_count   = 0;
    cfg.context_is_null   = 1;
    cfg.skip_comment_rows = 0;

    laplace_etl_session_t* sess = nullptr;
    ASSERT_EQ(laplace_etl_session_open(&cfg, &sess), 0);
    ASSERT_NE(sess, nullptr);
    intent_stage_t* stage = intent_stage_new(1u << 16);
    ASSERT_NE(stage, nullptr);

    std::vector<std::filesystem::path> files;
    for (auto& e : std::filesystem::recursive_directory_iterator(dir))
        if (e.is_regular_file() && e.path().extension() == ".tab") files.push_back(e.path());
    std::sort(files.begin(), files.end());

    laplace_etl_stats_t stats;
    std::memset(&stats, 0, sizeof(stats));
    for (auto& fp : files) {
        while (laplace_etl_session_feed_file(
                   sess, fp.string().c_str(), 1u << 20, 0, stage,
                   nullptr, nullptr, nullptr, nullptr, &stats) == 1) { /* more rows in file */ }
    }
    std::fprintf(stderr, "[repro] read=%llu parsed=%llu emitted=%llu ents=%zu phys=%zu (files=%zu)\n",
                 (unsigned long long)stats.rows_read, (unsigned long long)stats.rows_parsed,
                 (unsigned long long)stats.rows_emitted,
                 intent_stage_entity_count(stage), intent_stage_physicality_count(stage), files.size());
    laplace_etl_session_close(sess);
    intent_stage_free(stage);
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

// Cross-source convergence: a JSON scalar value and the same surface decomposed by the
// content path (WordNet/OMW/VerbNet via ContentWitnessBatch) must resolve to ONE entity.
// The JSON leaf must adopt laplace_content_root_id of its decoded content. Regression guard
// for the grammar_compose.cpp json-leaf -> content-root unification.
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

    // The canonical content id for the surface "New York" (what the content path mints).
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

// Regression: deeply nested input must not overflow the native stack. ast_walk is now iterative;
// the old recursion crashed (uncatchable) on trees thousands deep, and tree-sitter parses
// iteratively so it readily hands back such trees. 20k deep blows a 1 MiB stack with recursion.
// Returns true iff the JSON value `surface` (as it appears in `json_src`) composes to an entity whose
// id equals laplace_content_root_id(surface) — i.e. the JSON path and the content path converge.
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

// A2.4 convergence battery: JSON string values must resolve to the SAME entity the content path mints,
// across ASCII / multiword / CJK / non-ASCII NFC, AND an NFD form must converge to the NFC entity.
// Guards A1 (json leaf -> content root) together with A2.1 (NFC at the decomposer chokepoint).
TEST(GrammarCompose, ConvergenceBattery) {
    EXPECT_TRUE(json_value_converges("{\"k\":\"New York\"}", "New York"));      // multiword ASCII
    EXPECT_TRUE(json_value_converges("{\"k\":\"\xE6\x9D\xB1\xE4\xBA\xAC\"}",
                                     "\xE6\x9D\xB1\xE4\xBA\xAC"));               // 東京 (CJK)
    EXPECT_TRUE(json_value_converges("{\"k\":\"caf\xC3\xA9\"}", "caf\xC3\xA9")); // café, NFC (U+00E9)
    // café as NFD (cafe + U+0301 combining acute) must converge to the NFC café content id.
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
