#include <gtest/gtest.h>

#include <cstring>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>

#include "laplace/core/grammar_registry.h"
#include "laplace/core/grammar_decomposer.h"
#include "laplace/core/grammar_compose.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

namespace {

TEST(GrammarCompose, JsonStringDecodePreservesUnicodeScalarsAndEscapedControls) {
    const std::vector<std::pair<std::string, std::string>> cases = {
        {"", ""},
        {"ordinary text", "ordinary text"},
        {"\xCE\xBB\xE6\xA3\x8B", "\xCE\xBB\xE6\xA3\x8B"},
        {"cafe\xCC\x81", "cafe\xCC\x81"},
        {R"(\"\\\/\b\f\n\r\t)", "\"\\/\b\f\n\r\t"},
        {R"(\u0000\u001f\u0022\u005c)", std::string("\0\x1f\"\\", 4)},
        {R"(\u03bb\u68CB)", "\xCE\xBB\xE6\xA3\x8B"},
        {R"(\uD7FF\uE000\uFFFF)", "\xED\x9F\xBF\xEE\x80\x80\xEF\xBF\xBF"},
        {R"(\uD800\uDC00)", "\xF0\x90\x80\x80"},
        {R"(\uDBFF\uDFFF)", "\xF4\x8F\xBF\xBF"},
        {R"(before\ud83D\uDe00after)", "before\xF0\x9F\x98\x80" "after"},
        {R"(\ud83d\udc69\u200d\ud83d\udcbb)",
            "\xF0\x9F\x91\xA9\xE2\x80\x8D\xF0\x9F\x92\xBB"},
        {"\xF0\x9F\x98\x80", "\xF0\x9F\x98\x80"},
    };
    for (const auto& [input, expected] : cases) {
        SCOPED_TRACE(input);
        std::vector<uint8_t> output(expected.size() + 1, 0xA5);
        size_t written = SIZE_MAX;
        ASSERT_EQ(laplace_json_string_decode(
            reinterpret_cast<const uint8_t*>(input.data()), input.size(),
            output.data(), expected.size(), &written), 0);
        ASSERT_EQ(written, expected.size());
        EXPECT_EQ(std::string(reinterpret_cast<const char*>(output.data()), written), expected);
        EXPECT_EQ(output.back(), 0xA5) << "Decoder must not append a terminator";

        std::string in_place = input;
        ASSERT_EQ(laplace_json_string_decode(
            reinterpret_cast<const uint8_t*>(in_place.data()), in_place.size(),
            reinterpret_cast<uint8_t*>(in_place.data()), in_place.size(), &written), 0);
        EXPECT_EQ(in_place.substr(0, written), expected);
    }
}

TEST(GrammarCompose, JsonStringDecodeRejectsMalformedEscapesAndLiteralUtf8) {
    const std::vector<std::string> cases = {
        "\\", R"(\q)", R"(\x41)", R"(\U0001F600)",
        R"(\u)", R"(\u0)", R"(\u00)", R"(\u000)", R"(\u00G0)",
        R"(\uD800)", R"(\uDBFF)", R"(\uDC00)", R"(\uDFFF)",
        R"(\uD800x)", R"(\uD800\n)", R"(\uD800\u0000)",
        R"(\uD800\uD800)", R"(\uD800\uDC0)", R"(\uD800\uDC0Z)",
        R"(\uDC00\uD800)", R"(\uD800 \uDC00)", R"(valid prefix\q)",
        "raw\"quote", "raw\nnewline", std::string("raw\0nul", 7),
        "\x80", "\xC0\x80", "\xC2", "\xC2x", "\xE0\x80\x80",
        "\xED\xA0\x80", "\xED\xBF\xBF", "\xF0\x80\x80\x80",
        "\xF4\x90\x80\x80", "\xF5\x80\x80\x80", "\xF0\x9F\x98", "\xFF",
    };
    for (const auto& input : cases) {
        SCOPED_TRACE(input);
        std::vector<uint8_t> output(input.size() + 1, 0xA5);
        size_t written = SIZE_MAX;
        EXPECT_EQ(laplace_json_string_decode(
            reinterpret_cast<const uint8_t*>(input.data()), input.size(),
            output.data(), input.size(), &written), -1);
        EXPECT_EQ(written, 0u);
        EXPECT_EQ(output.back(), 0xA5);
        written = SIZE_MAX;
        EXPECT_EQ(laplace_json_string_decode(
            reinterpret_cast<const uint8_t*>(input.data()), input.size(),
            nullptr, 0, &written), -1) << "Malformed input must not become a size result";
        EXPECT_EQ(written, 0u);
    }
}

TEST(GrammarCompose, JsonStringDecodeReportsCapacityAndValidatesArguments) {
    const std::string input = R"(x\uD83D\uDE00y)";
    const std::string expected = "x\xF0\x9F\x98\x80y";
    for (size_t capacity = 0; capacity < expected.size(); ++capacity) {
        std::vector<uint8_t> output(capacity + 1, 0xA5);
        size_t written = SIZE_MAX;
        EXPECT_EQ(laplace_json_string_decode(
            reinterpret_cast<const uint8_t*>(input.data()), input.size(),
            output.data(), capacity, &written), -2);
        EXPECT_EQ(written, expected.size());
        EXPECT_EQ(output.back(), 0xA5) << "Insufficient capacity must not overwrite the guard";
    }
    size_t written = SIZE_MAX;
    EXPECT_EQ(laplace_json_string_decode(nullptr, 0, nullptr, 0, &written), 0);
    EXPECT_EQ(written, 0u);
    EXPECT_EQ(laplace_json_string_decode(
        reinterpret_cast<const uint8_t*>(input.data()), input.size(), nullptr, 0, &written), -2);
    EXPECT_EQ(written, expected.size());
    uint8_t output = 0xA5;
    EXPECT_EQ(laplace_json_string_decode(nullptr, 1, &output, 1, &written), -1);
    EXPECT_EQ(written, 0u);
    EXPECT_EQ(laplace_json_string_decode(nullptr, 0, nullptr, 1, &written), -1);
    EXPECT_EQ(written, 0u);
    EXPECT_EQ(laplace_json_string_decode(nullptr, 0, &output, 1, nullptr), -1);
    EXPECT_EQ(output, 0xA5);
}

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







TEST(GrammarCompose, ProbeMaterializationPreservesFullBodiesAndOccurrenceMultiplicity) {
    struct Case {
        const char* modality;
        const char* source;
        bool repeated_grapheme;
        bool partial_json_floor = false;
    };
    const Case cases[] = {
        {"tsv", "1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}", false},
        {"tsv", "7\tIsA\t/c/en/a moment in time\t/c/en/moment\t{}", false},
        {"tsv", "a\tb\tc\n", false},
        {"tsv", "a\ta\ta\na\ta\ta\n", false},
        {"tsv", "q\xCC\x81\tq\xCC\x81\n", true},
        {"json", R"({"name":"dog","same":"dog"})", false},
        {"json", R"({"name":"q\u0301"})", false, true},
        {"json", R"({"name":"q\u0301","same":"q\u0301"})", true, true},
        {"json", "{\"name\":\"q\xCC\x81\",\"same\":\"q\xCC\x81\"}", true, true},
        {"json", R"({"name":"\ud83d\udc69\u200d\ud83d\udcbb"})", false, true},
        {"json", R"("q\u0301")", false},

    };
    for (const auto& fixture : cases) {
        SCOPED_TRACE(::testing::Message() << fixture.modality << ": " << fixture.source);
        const auto* bytes = reinterpret_cast<const uint8_t*>(fixture.source);
        const size_t length = std::strlen(fixture.source);
        const TSLanguage* recipe = laplace_grammar_lookup_by_id(fixture.modality);
        ASSERT_NE(recipe, nullptr);
        laplace_ast_t* ast = nullptr;
        ASSERT_EQ(laplace_grammar_parse(bytes, length, recipe, &ast), 0);
        std::unique_ptr<laplace_ast_t, decltype(&laplace_ast_free)> ast_owner(ast, laplace_ast_free);
        hash128_t source_id{}, type_meta{};
        hash128_blake3_str("test/grammar-probe-forms", &source_id);
        hash128_blake3_str("Type", &type_meta);

        laplace_compose_result_t* full = nullptr;
        ASSERT_EQ(laplace_grammar_compose(bytes, length, ast, fixture.modality,
                                            source_id, type_meta, &full), 0);
        std::unique_ptr<laplace_compose_result_t, decltype(&laplace_compose_result_free)>
            full_owner(full, laplace_compose_result_free);
        ASSERT_NE(full, nullptr);
        laplace_compose_result_t* probe = nullptr;
        ASSERT_EQ(laplace_grammar_compose_probe(bytes, length, ast, fixture.modality,
                                                  source_id, type_meta, &probe), 0);
        std::unique_ptr<laplace_compose_result_t, decltype(&laplace_compose_result_free)>
            probe_owner(probe, laplace_compose_result_free);
        ASSERT_NE(probe, nullptr);
        const size_t entity_count = laplace_compose_entity_count(probe);
        const hash128_t root = laplace_compose_root_id(full);
        const hash128_t probed_root = laplace_compose_root_id(probe);
        ASSERT_TRUE(hash128_equals(&root, &probed_root));

        if (fixture.partial_json_floor) {
            ASSERT_GT(laplace_compose_physicality_count(probe), 0u)
                << "fixture must retain real grapheme forms during probing";
            ASSERT_LT(laplace_compose_physicality_count(probe),
                      laplace_compose_physicality_count(full))
                << "floor forms alone cannot prove the AST body is complete";
        }
        ASSERT_EQ(laplace_grammar_compose_materialize_phys(
            probe, bytes, length, ast, fixture.modality), 0);
        ASSERT_EQ(laplace_compose_entity_count(probe), entity_count);
        ASSERT_EQ(laplace_compose_entity_count(full), entity_count);
        const size_t count = laplace_compose_physicality_count(full);
        ASSERT_GT(count, 0u);
        ASSERT_EQ(laplace_compose_physicality_count(probe), count);
        size_t root_forms = 0;
        size_t repeated_forms = 0;
        for (size_t i = 0; i < count; ++i) {
            laplace_compose_physicality_t expected{}, actual{};
            ASSERT_EQ(laplace_compose_get_physicality(full, i, &expected), 0);
            ASSERT_EQ(laplace_compose_get_physicality(probe, i, &actual), 0);
            EXPECT_TRUE(hash128_equals(&expected.id, &actual.id));
            EXPECT_TRUE(hash128_equals(&expected.entity_id, &actual.entity_id));
            EXPECT_EQ(std::memcmp(expected.coord, actual.coord, sizeof(expected.coord)), 0);
            EXPECT_EQ(std::memcmp(&expected.hilbert, &actual.hilbert, sizeof(expected.hilbert)), 0);
            EXPECT_EQ(expected.n_constituents, actual.n_constituents);
            ASSERT_EQ(expected.trajectory_n, actual.trajectory_n);
            EXPECT_EQ(std::memcmp(expected.trajectory_xyzm, actual.trajectory_xyzm,
                                  expected.trajectory_n * sizeof(double)), 0);
            if (hash128_equals(&expected.entity_id, &root)) ++root_forms;
            for (size_t j = 0; j < i; ++j) {
                laplace_compose_physicality_t previous{};
                ASSERT_EQ(laplace_compose_get_physicality(full, j, &previous), 0);
                if (hash128_equals(&expected.id, &previous.id)) {
                    ++repeated_forms;
                    break;
                }
            }
        }
        EXPECT_EQ(root_forms, 1u) << "a unary AST wrapper must not add a self-form";
        if (fixture.repeated_grapheme)
            EXPECT_GT(repeated_forms, 0u) << "raw repeated occurrences must survive";
        ASSERT_EQ(laplace_grammar_compose_materialize_phys(
            probe, bytes, length, ast, fixture.modality), 0);
        EXPECT_EQ(laplace_compose_physicality_count(probe), count)
            << "materializing the same owned result twice must not append observations";
        ASSERT_EQ(laplace_grammar_compose_materialize_phys(
            full, bytes, length, ast, fixture.modality), 0);
        EXPECT_EQ(laplace_compose_physicality_count(full), count)
            << "a fully composed result must already be complete";

        // Drain actual owner output with and without exact E presence. P tuples
        // (including placement IDs and occurrence order) stay exactly identical.
        std::unique_ptr<intent_stage_t,decltype(&intent_stage_free)> all_stage(
            intent_stage_new(0),intent_stage_free), known_stage(intent_stage_new(0),intent_stage_free);
        std::vector<uint8_t> present((entity_count+7)/8,255);
        ASSERT_EQ(0,laplace_compose_drain_into_stage(full,all_stage.get(),&source_id,
            INTENT_STAGE_PG_EPOCH_UNIX_US,1.0,nullptr,0));
        ASSERT_EQ(0,laplace_compose_drain_into_stage(probe,known_stage.get(),&source_id,
            INTENT_STAGE_PG_EPOCH_UNIX_US,1.0,present.data(),entity_count));
        EXPECT_EQ(0u,intent_stage_entity_count(known_stage.get()));
        size_t full_bytes=0,known_bytes=0;
        const auto* full_p=intent_stage_tuple_ptr(all_stage.get(),INTENT_STAGE_TABLE_PHYSICALITIES,&full_bytes);
        const auto* known_p=intent_stage_tuple_ptr(known_stage.get(),INTENT_STAGE_TABLE_PHYSICALITIES,&known_bytes);
        ASSERT_EQ(full_bytes,known_bytes);
        EXPECT_EQ(0,std::memcmp(full_p,known_p,full_bytes));
    }
}

// GH #595: the span index must resolve every emitted occurrence at scale.
// Occurrence spans are not entity identities: all CSV commas reuse one
// codepoint, while the 3,000 different cell values retain different content IDs.

TEST(GrammarCompose, EscapedJsonStringsConvergeThroughNativeComposition) {
    struct Case {
        const char* escaped;
        const char* literal;
        const char* decoded;
    };
    const Case cases[] = {
        {R"("q\u0301")", "\"q\xCC\x81\"", "q\xCC\x81"},
        {R"("a q\u0301 tail")", "\"a q\xCC\x81 tail\"", "a q\xCC\x81 tail"},
        {R"("\u0063at")", R"("cat")", "cat"},
        {R"("\u0061")", R"("a")", "a"},
        {R"("caf\u00e9")", "\"caf\xC3\xA9\"", "caf\xC3\xA9"},
        {R"("\ud83d\udc69\u200d\ud83d\udcbb")",
         "\"\xF0\x9F\x91\xA9\xE2\x80\x8D\xF0\x9F\x92\xBB\"",
         "\xF0\x9F\x91\xA9\xE2\x80\x8D\xF0\x9F\x92\xBB"},
    };
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("json");
    ASSERT_NE(recipe, nullptr);
    hash128_t source{}, type{};
    hash128_blake3_str("test/json-complete-string", &source);
    hash128_blake3_str("Type", &type);

    for (const auto& fixture : cases) {
        SCOPED_TRACE(fixture.escaped);
        hash128_t expected{};
        ASSERT_EQ(laplace_content_root_id(
            reinterpret_cast<const uint8_t*>(fixture.decoded),
            std::strlen(fixture.decoded), &expected), 0);
        for (bool object : {false, true}) {
            SCOPED_TRACE(object ? "object value" : "standalone scalar");
            using Result = std::unique_ptr<laplace_compose_result_t,
                                          decltype(&laplace_compose_result_free)>;
            Result full[] = {Result(nullptr, laplace_compose_result_free),
                             Result(nullptr, laplace_compose_result_free)};
            const char* spellings[] = {fixture.escaped, fixture.literal};
            for (size_t spelling = 0; spelling < 2; ++spelling) {
                const std::string text = object
                    ? std::string("{\"k\":") + spellings[spelling] + "}"
                    : spellings[spelling];
                const auto* bytes = reinterpret_cast<const uint8_t*>(text.data());
                laplace_ast_t* ast = nullptr;
                ASSERT_EQ(laplace_grammar_parse(bytes, text.size(), recipe, &ast), 0);
                std::unique_ptr<laplace_ast_t, decltype(&laplace_ast_free)>
                    ast_owner(ast, laplace_ast_free);
                const uint32_t start = object ? 5u : 0u;
                const uint32_t end = static_cast<uint32_t>(text.size() - (object ? 1u : 0u));
                bool found_string = false;
                for (size_t i = 0; i < laplace_ast_node_count(ast); ++i) {
                    laplace_ast_node_t node{};
                    ASSERT_EQ(laplace_ast_get_node(ast, i, &node), 0);
                    const char* name = laplace_ast_type_name(ast, node.type_id);
                    if (!name || std::strcmp(name, "string") != 0 ||
                        node.start_byte != start || node.end_byte != end) continue;
                    found_string = true;
                    hash128_t node_id{};
                    ASSERT_EQ(laplace_grammar_compose_node_id(
                        bytes, text.size(), ast, "json", i, &node_id, nullptr), 0);
                    EXPECT_TRUE(hash128_equals(&node_id, &expected))
                        << "native string identity must include every decoded escape";
                }
                ASSERT_TRUE(found_string);
                laplace_compose_result_t* raw = nullptr;
                ASSERT_EQ(laplace_grammar_compose(
                    bytes, text.size(), ast, "json", source, type, &raw), 0);
                full[spelling].reset(raw);
                ASSERT_NE(raw, nullptr);
                hash128_t span_id{};
                ASSERT_EQ(laplace_compose_span_lookup(raw, start, end, &span_id), 0);
                EXPECT_TRUE(hash128_equals(&span_id, &expected))
                    << "direct native span lookup must agree without JSON helper decoding";
                hash128_t quick_root{};
                ASSERT_EQ(laplace_grammar_compose_row_root(
                    bytes, text.size(), ast, "json", &quick_root, nullptr), 0);
                const hash128_t root = laplace_compose_root_id(raw);
                EXPECT_TRUE(hash128_equals(&root, &quick_root));
                if (!object) EXPECT_TRUE(hash128_equals(&root, &expected));
                laplace_compose_result_t* probe = nullptr;
                ASSERT_EQ(laplace_grammar_compose_probe(
                    bytes, text.size(), ast, "json", source, type, &probe), 0);
                Result probe_owner(probe, laplace_compose_result_free);
                ASSERT_NE(probe, nullptr);
                const hash128_t probe_root = laplace_compose_root_id(probe);
                EXPECT_TRUE(hash128_equals(&root, &probe_root));
                ASSERT_EQ(laplace_grammar_compose_materialize_phys(
                    probe, bytes, text.size(), ast, "json"), 0);
                EXPECT_EQ(laplace_compose_physicality_count(probe),
                          laplace_compose_physicality_count(raw));
            }
            const hash128_t escaped_root = laplace_compose_root_id(full[0].get());
            const hash128_t literal_root = laplace_compose_root_id(full[1].get());
            EXPECT_TRUE(hash128_equals(&escaped_root, &literal_root))
                << "record composition must not depend on the JSON escape spelling";
            std::vector<laplace_compose_entity_t> persisted[2];
            for (size_t spelling = 0; spelling < 2; ++spelling) {
                for (size_t i = 0; i < laplace_compose_entity_count(full[spelling].get()); ++i) {
                    laplace_compose_entity_t entity{};
                    ASSERT_EQ(laplace_compose_get_entity(full[spelling].get(), i, &entity), 0);
                    if (!entity.packaging) persisted[spelling].push_back(entity);
                }
            }
            ASSERT_EQ(persisted[0].size(), persisted[1].size());
            ASSERT_FALSE(persisted[0].empty());
            for (size_t i = 0; i < persisted[0].size(); ++i) {
                EXPECT_TRUE(hash128_equals(&persisted[0][i].id, &persisted[1][i].id));
                EXPECT_EQ(persisted[0][i].tier, persisted[1][i].tier);
                EXPECT_TRUE(hash128_equals(&persisted[0][i].type_id, &persisted[1][i].type_id));
            }
            if (!object) {
                tier_tree_t* content = nullptr;
                ASSERT_EQ(content_witness_tree_build(
                    reinterpret_cast<const uint8_t*>(fixture.decoded),
                    std::strlen(fixture.decoded), &content), 0);
                std::unique_ptr<tier_tree_t, decltype(&tier_tree_free)>
                    content_owner(content, tier_tree_free);
                tier_node_view_t natural{};
                ASSERT_EQ(content_witness_tree_root_node(content, &natural), 0);
                size_t root_entities = 0;
                const hash128_t natural_type = laplace_content_tier_type_id(natural.tier);
                for (const auto& entity : persisted[0]) {
                    if (!hash128_equals(&entity.id, &expected)) continue;
                    ++root_entities;
                    EXPECT_EQ(entity.tier, natural.tier);
                    EXPECT_TRUE(hash128_equals(&entity.type_id, &natural_type));
                }
                EXPECT_EQ(root_entities, 1u);
            }
            const size_t count = laplace_compose_physicality_count(full[0].get());
            ASSERT_GT(count, 0u);
            ASSERT_EQ(count, laplace_compose_physicality_count(full[1].get()));
            for (size_t i = 0; i < count; ++i) {
                laplace_compose_physicality_t escaped{}, literal{};
                ASSERT_EQ(laplace_compose_get_physicality(full[0].get(), i, &escaped), 0);
                ASSERT_EQ(laplace_compose_get_physicality(full[1].get(), i, &literal), 0);
                EXPECT_TRUE(hash128_equals(&escaped.id, &literal.id));
                EXPECT_TRUE(hash128_equals(&escaped.entity_id, &literal.entity_id));
                EXPECT_EQ(std::memcmp(escaped.coord, literal.coord, sizeof(escaped.coord)), 0);
                EXPECT_EQ(std::memcmp(&escaped.hilbert, &literal.hilbert,
                                      sizeof(escaped.hilbert)), 0);
                EXPECT_EQ(escaped.n_constituents, literal.n_constituents);
                ASSERT_EQ(escaped.trajectory_n, literal.trajectory_n);
                EXPECT_EQ(std::memcmp(escaped.trajectory_xyzm, literal.trajectory_xyzm,
                                      escaped.trajectory_n * sizeof(double)), 0);
            }
        }
    }
}

TEST(GrammarCompose, InvalidEscapedJsonScalarDoesNotSilentlyDropItsValue) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("json");
    ASSERT_NE(recipe, nullptr);
    const char* cases[] = {R"({"k":"q\ud800"})", R"({"k":"\udc00"})"};
    for (const char* text : cases) {
        SCOPED_TRACE(text);
        const auto* bytes = reinterpret_cast<const uint8_t*>(text);
        const size_t length = std::strlen(text);
        laplace_ast_t* ast = nullptr;
        ASSERT_EQ(laplace_grammar_parse(bytes, length, recipe, &ast), 0);
        std::unique_ptr<laplace_ast_t, decltype(&laplace_ast_free)>
            ast_owner(ast, laplace_ast_free);
        hash128_t source{}, type{};
        for (bool probe : {false, true}) {
            laplace_compose_result_t* result = nullptr;
            const int rc = probe
                ? laplace_grammar_compose_probe(bytes, length, ast, "json", source, type, &result)
                : laplace_grammar_compose(bytes, length, ast, "json", source, type, &result);
            std::unique_ptr<laplace_compose_result_t, decltype(&laplace_compose_result_free)>
                owner(result, laplace_compose_result_free);
            EXPECT_LT(rc, 0);
            EXPECT_EQ(result, nullptr);
        }
    }
}

TEST(GrammarCompose, SpanLookupResolvesEveryDistinctSpanAtScale) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("csv");
    ASSERT_NE(recipe, nullptr);

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
    ASSERT_NE(result->span_index, nullptr);
    ASSERT_GT(result->span_index_cap, 0u);

    // The emitted native span records are independent of the lookup index.
    // Keep the first record for a repeated span, matching the public lookup's
    // documented linear-scan fallback, without reproducing its hash/probe code.
    using Span = std::pair<uint32_t, uint32_t>;
    std::map<Span, hash128_t> expected_spans;
    for (size_t i = 0; i < result->span_count; ++i) {
        const auto& span = result->spans[i];
        expected_spans.emplace(Span{span.start_byte, span.end_byte}, span.entity_id);
    }
    ASSERT_GT(expected_spans.size(), static_cast<size_t>(kCols * kRows));

    // Query the AST's actual spans, including repeated canonical content.
    const size_t node_count = laplace_ast_node_count(ast);
    ASSERT_GT(node_count, static_cast<size_t>(kCols * kRows));
    std::vector<Span> spans;
    for (size_t i = 0; i < node_count; ++i) {
        laplace_ast_node_t node;
        ASSERT_EQ(laplace_ast_get_node(ast, i, &node), 0);
        spans.emplace_back(node.start_byte, node.end_byte);
    }
    std::sort(spans.begin(), spans.end());
    spans.erase(std::unique(spans.begin(), spans.end()), spans.end());

    hash128_t comma_id;
    ASSERT_EQ(laplace_content_root_id(
        reinterpret_cast<const uint8_t*>(","), 1, &comma_id), 0);
    size_t resolved = 0;
    size_t comma_spans = 0;
    std::vector<hash128_t> cell_ids;
    for (const auto& [start, end] : spans) {
        SCOPED_TRACE(::testing::Message() << "span [" << start << "," << end << ")");
        const auto expected = expected_spans.find(Span{start, end});
        hash128_t id{};
        const int lookup = laplace_compose_span_lookup(result, start, end, &id);
        if (expected == expected_spans.end()) {
            EXPECT_NE(lookup, 0) << "lookup fabricated an unemitted span";
            continue;
        }
        ASSERT_EQ(lookup, 0) << "index dropped an emitted span";
        EXPECT_TRUE(hash128_equals(&id, &expected->second))
            << "index returned another occurrence's entity";
        ++resolved;

        ASSERT_LE(start, end);
        ASSERT_LE(static_cast<size_t>(end), src.size());
        const std::string surface = src.substr(start, end - start);
        if (surface == ",") {
            EXPECT_TRUE(hash128_equals(&id, &comma_id))
                << "identical punctuation acquired a different entity ID";
            ++comma_spans;
        } else if (surface.size() > 1 && surface[0] == 'v' &&
                   std::all_of(surface.begin() + 1, surface.end(),
                       [](char c) { return c >= '0' && c <= '9'; })) {
            hash128_t content_id;
            ASSERT_EQ(laplace_content_root_id(
                reinterpret_cast<const uint8_t*>(surface.data()),
                surface.size(), &content_id), 0);
            EXPECT_TRUE(hash128_equals(&id, &content_id))
                << "cell lookup differs from its independent content composition";
            cell_ids.push_back(id);
        }
    }
    EXPECT_EQ(resolved, expected_spans.size());
    EXPECT_EQ(comma_spans, static_cast<size_t>(kRows * (kCols - 1)));
    ASSERT_EQ(cell_ids.size(), static_cast<size_t>(kCols * kRows));
    std::sort(cell_ids.begin(), cell_ids.end(), [](const hash128_t& a, const hash128_t& b) {
        return a.hi != b.hi ? a.hi < b.hi : a.lo < b.lo;
    });
    const size_t distinct_cells = std::unique(cell_ids.begin(), cell_ids.end(),
        [](const hash128_t& a, const hash128_t& b) {
            return hash128_equals(&a, &b);
        }) - cell_ids.begin();
    EXPECT_EQ(distinct_cells, cell_ids.size())
        << "different cell contents aliased to one entity";

    hash128_t missing{};
    EXPECT_NE(laplace_compose_span_lookup(
        result, static_cast<uint32_t>(src.size()),
        static_cast<uint32_t>(src.size() + 1), &missing), 0);
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

TEST(GrammarSourceCompose, EmptyParserSpansDoNotBecomeContentOrRejectTheSource) {
    // Empty nodes include both normal grammar bookkeeping and recovery tokens.
    // Neither represents source bytes. A whitespace-only parser root still has
    // an entire physical artifact outside its empty syntax span to preserve.
    const std::pair<const char*, const char*> cases[] = {
        {"sql", "\n"},
        {"c", "#ifndef HEADER_H\n#define HEADER_H\nint value;\n"},
        {"markdown", "- first line\n  continued line\n\n  another paragraph\n"},
        {"typescript", "export const view = <div>{value}</div>;\n"},
    };
    size_t empty_spans = 0;
    for (const auto& [modality, source] : cases) {
        SCOPED_TRACE(modality);
        const size_t len = std::strlen(source);
        const auto* bytes = reinterpret_cast<const uint8_t*>(source);
        laplace_ast_t* ast = nullptr;
        ASSERT_EQ(laplace_grammar_parse(bytes, len,
                      laplace_grammar_lookup_by_id(modality), &ast), 0);
        std::unique_ptr<laplace_ast_t, decltype(&laplace_ast_free)> ast_owner(ast, laplace_ast_free);
        laplace_compose_result_t* result = nullptr;
        ASSERT_EQ(laplace_grammar_source_compose(bytes, len, ast, modality, &result), 0);
        std::unique_ptr<laplace_compose_result_t, decltype(&laplace_compose_result_free)>
            result_owner(result, laplace_compose_result_free);
        ASSERT_NE(result, nullptr);
        for (size_t i = 0; i < laplace_ast_node_count(ast); ++i) {
            laplace_ast_node_t node;
            ASSERT_EQ(laplace_ast_get_node(ast, i, &node), 0);
            hash128_t id{};
            int found = laplace_compose_span_lookup(result, node.start_byte, node.end_byte, &id);
            if (node.start_byte == node.end_byte) {
                ++empty_spans;
                EXPECT_NE(found, 0) << "parser-invented empty token became content";
            } else {
                EXPECT_EQ(found, 0) << "source-backed AST span was lost at " << i;
            }
        }
        const hash128_t root = laplace_compose_root_id(result);
        const hash128_t zero{};
        EXPECT_FALSE(hash128_equals(&root, &zero));
        if (len == 1) {
            hash128_t expected;
            ASSERT_EQ(laplace_content_source_root_id(bytes, len, &expected), 0);
            EXPECT_TRUE(hash128_equals(&root, &expected));
        }
    }
    EXPECT_GE(empty_spans, 3u) << "fixtures must exercise empty parser nodes";
}

TEST(GrammarSourceCompose, RepositoryCSourceSharesLexicalTreesAndPreservesEveryLeafSpan) {
    const auto path = std::filesystem::path(__FILE__).parent_path().parent_path()
        / "src" / "tier_tree.c";
    std::ifstream input(path, std::ios::binary);
    ASSERT_TRUE(input.is_open()) << path;
    const std::string source((std::istreambuf_iterator<char>(input)),
                              std::istreambuf_iterator<char>());
    ASSERT_FALSE(source.empty());
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("c");
    ASSERT_NE(recipe, nullptr);
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(source.data()),
                                     source.size(), recipe, &ast), 0);
    std::unique_ptr<laplace_ast_t, decltype(&laplace_ast_free)> ast_owner(ast, laplace_ast_free);
    laplace_compose_result_t* result = nullptr;
    ASSERT_EQ(laplace_grammar_source_compose(reinterpret_cast<const uint8_t*>(source.data()),
                                             source.size(), ast, "c", &result), 0);
    std::unique_ptr<laplace_compose_result_t, decltype(&laplace_compose_result_free)>
        result_owner(result, laplace_compose_result_free);

    const size_t count = laplace_ast_node_count(ast);
    std::vector<laplace_ast_node_t> nodes(count);
    std::vector<bool> has_children(count, false);
    for (size_t i = 0; i < count; ++i) {
        ASSERT_EQ(laplace_ast_get_node(ast, i, &nodes[i]), 0);
        if (nodes[i].parent != LAPLACE_AST_ROOT) has_children[nodes[i].parent] = true;
    }
    size_t leaves = 0;
    std::map<std::string, hash128_t> lexical_roots;
    for (size_t i = 0; i < count; ++i) {
        hash128_t actual;
        ASSERT_EQ(laplace_compose_span_lookup(result, nodes[i].start_byte,
                                                nodes[i].end_byte, &actual), 0);
        if (has_children[i]) continue;
        ++leaves;
        std::string bytes = source.substr(nodes[i].start_byte,
                                            nodes[i].end_byte - nodes[i].start_byte);
        auto [entry, added] = lexical_roots.try_emplace(bytes);
        if (added)
            ASSERT_EQ(laplace_content_source_root_id(reinterpret_cast<const uint8_t*>(bytes.data()),
                                                       bytes.size(), &entry->second), 0);
        EXPECT_TRUE(hash128_equals(&actual, &entry->second)) << "AST leaf " << i;
    }
    // The real file contains repeated identifiers and punctuation. Shared lexical
    // decomposition must reduce retained trees without dropping occurrence spans.
    ASSERT_GT(leaves, lexical_roots.size());
    EXPECT_LT(result->source_tree_count, leaves);
    EXPECT_GE(result->span_count, count);
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
