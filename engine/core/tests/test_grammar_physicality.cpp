#include <gtest/gtest.h>

#include <array>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/grammar_compose.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/trajectory.h"

extern "C" const TSLanguage* tree_sitter_pgn(void);

namespace {

using Ast = std::unique_ptr<laplace_ast_t, decltype(&laplace_ast_free)>;
using Result = std::unique_ptr<laplace_compose_result_t, decltype(&laplace_compose_result_free)>;
using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
using Capture = std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>;
constexpr size_t kBudget = 64u * 1024u * 1024u;
const hash128_t kSourceA{0x1234, 0x5678};
const hash128_t kSourceB{0x9876, 0x5432};
const char* kGame = "[Event \"Example\"]\n1. e4 e5 *\n";

class GrammarPhysicality : public ::testing::Test {
protected:
    std::unique_ptr<physicality_descriptor_vocabulary_t, decltype(&physicality_descriptor_vocabulary_free)>
        vocabulary{nullptr, physicality_descriptor_vocabulary_free};

    void SetUp() override {
        ASSERT_TRUE(codepoint_table_is_loaded());
        physicality_descriptor_vocabulary_t* raw = nullptr;
        ASSERT_EQ(physicality_descriptor_vocabulary_create(&kSourceA, kBudget, &raw),
            PHYSICALITY_DESCRIPTOR_OK);
        vocabulary.reset(raw);
    }

    Ast parse(const std::string& source) {
        laplace_ast_t* raw = nullptr;
        EXPECT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(source.data()),
            source.size(), tree_sitter_pgn(), &raw), 0);
        Ast ast(raw, laplace_ast_free);
        if (!ast) return ast;
        laplace_ast_diagnostics_t diagnostics{};
        EXPECT_EQ(laplace_ast_get_diagnostics(ast.get(), &diagnostics), 0);
        EXPECT_EQ(diagnostics.root_has_error, 0u);
        EXPECT_EQ(diagnostics.error_node_count, 0u);
        EXPECT_EQ(diagnostics.missing_node_count, 0u);
        return ast;
    }

    Result compose(const std::string& source, const Ast& ast, bool source_representation = false) {
        laplace_compose_result_t* raw = nullptr;
        const auto* bytes = reinterpret_cast<const uint8_t*>(source.data());
        const int status = source_representation ?
            laplace_grammar_source_compose(bytes, source.size(), ast.get(), "pgn", &raw) :
            laplace_grammar_compose(bytes, source.size(), ast.get(), "pgn", kSourceA, {}, &raw);
        EXPECT_EQ(status, 0);
        return Result(raw, laplace_compose_result_free);
    }

    Capture capture(const Stage& stage) {
        const intent_stage_t* stages[] = {stage.get()};
        const physicality_descriptor_limits_t limits{kBudget};
        physicality_descriptor_capture_t* raw = nullptr;
        EXPECT_EQ(physicality_descriptor_capture_stages(stages, 1,
            physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits, kBudget, &raw),
            PHYSICALITY_DESCRIPTOR_OK);
        return Capture(raw, physicality_descriptor_capture_free);
    }

    void expect_body(const physicality_descriptor_input_t& body,
                     const laplace_compose_physicality_t& expected) {
        EXPECT_TRUE(hash128_equals(&body.entity_id, &expected.entity_id));
        EXPECT_EQ(body.type, 1);
        EXPECT_EQ(std::memcmp(body.coord, expected.coord, sizeof(expected.coord)), 0);
        EXPECT_EQ(std::memcmp(&body.hilbert_index, &expected.hilbert, sizeof(expected.hilbert)), 0);
        ASSERT_EQ(body.trajectory_vertices * 4u, expected.trajectory_n);
        EXPECT_EQ(body.n_constituents, static_cast<int32_t>(expected.n_constituents));
        EXPECT_EQ(std::memcmp(body.trajectory_xyzm, expected.trajectory_xyzm,
            expected.trajectory_n * sizeof(double)), 0);
    }
};

TEST_F(GrammarPhysicality, DistinctSourcesRetainEveryComputedBodyAndFirstEntityWitness) {
    const std::string source = kGame;
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto result = compose(source, ast);
    ASSERT_NE(result, nullptr);
    ASSERT_GT(result->phys_count, 0u);
    Stage stage(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceA,
        10, 1.0, nullptr, 0), 0);
    const size_t first_entities = intent_stage_entity_count(stage.get());
    ASSERT_GT(first_entities, 0u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), result->phys_count);
    size_t entity_bytes = 0;
    const auto* entities = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ENTITIES, &entity_bytes);
    const std::vector<uint8_t> first_entity_rows(entities, entities + entity_bytes);
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceB,
        20, 1.0, nullptr, 0), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), first_entities);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), result->phys_count * 2u);
    entities = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ENTITIES, &entity_bytes);
    EXPECT_EQ(std::vector<uint8_t>(entities, entities + entity_bytes), first_entity_rows);
    auto captured = capture(stage);
    ASSERT_NE(captured, nullptr);
    size_t count = 0;
    const auto* inputs = physicality_descriptor_capture_inputs(captured.get(), &count);
    ASSERT_EQ(count, result->phys_count * 2u);
    const auto* observations = physicality_descriptor_capture_observations(captured.get(), nullptr);
    const auto* roots = physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(captured.get()), nullptr);
    for (size_t i = 0; i < result->phys_count; ++i) {
        expect_body(inputs[i], result->physicalities[i]);
        expect_body(inputs[result->phys_count + i], result->physicalities[i]);
        EXPECT_EQ(observations[i].observed_at_unix_us, 10);
        EXPECT_EQ(observations[result->phys_count + i].observed_at_unix_us, 20);
        EXPECT_TRUE(hash128_equals(&roots[i], &roots[result->phys_count + i]));
    }
}

TEST_F(GrammarPhysicality, ExistingBitmapAndSeenPlacementCannotSuppressComputedPhysicalities) {
    const std::string source = kGame;
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto result = compose(source, ast);
    ASSERT_NE(result, nullptr);
    ASSERT_GT(result->phys_count, 0u);
    ASSERT_NE(result->tree, nullptr);
    const size_t nodes = tier_tree_node_count(result->tree);
    ASSERT_EQ(nodes, result->entity_count);
    std::vector<uint8_t> present((nodes + 7u) / 8u, 0xff);
    Stage stage(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    for (size_t i = 0; i < result->phys_count; ++i)
        ASSERT_GE(intent_stage_witness_record(stage.get(), &result->physicalities[i].id), 0);
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceA,
        42, 1.0, present.data(), nodes), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 0u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), result->phys_count);
    auto captured = capture(stage);
    ASSERT_NE(captured, nullptr);
    size_t count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(captured.get(), &count);
    ASSERT_EQ(count, result->phys_count);
    for (size_t i = 0; i < count; ++i) expect_body(bodies[i], result->physicalities[i]);
}

TEST_F(GrammarPhysicality, AlternateNativeCoordinateAtSamePlacementSurvivesDrainAndDescriptorCapture) {
    const std::string source = kGame;
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto original = compose(source, ast);
    auto changed = compose(source, ast);
    ASSERT_NE(original, nullptr);
    ASSERT_NE(changed, nullptr);
    ASSERT_GT(original->phys_count, 0u);
    ASSERT_EQ(original->phys_count, changed->phys_count);
    auto& alternate = changed->physicalities[0];
    ASSERT_GE(alternate.n_constituents, 2u);
    std::vector<hash128_t> children(alternate.n_constituents);
    ASSERT_EQ(trajectory_constituents(alternate.trajectory_xyzm, alternate.trajectory_n / 4u,
        children.data(), children.size()), static_cast<int>(children.size()));
    // Controlled alternate child geometry, composed by the canonical native
    // owner; the AST, ordered child identities and legacy placement stay fixed.
    std::vector<double> coords(children.size() * 4u, 0.0);
    for (size_t i = 0; i < children.size(); ++i) coords[i * 4u] = 0.125;
    hash128_t recomposed{};
    hash_composer_compose_node(4, children.data(), coords.data(), children.size(),
        &recomposed, alternate.coord, &alternate.hilbert);
    ASSERT_TRUE(hash128_equals(&recomposed, &alternate.entity_id));
    ASSERT_NE(std::memcmp(alternate.coord, original->physicalities[0].coord, sizeof(alternate.coord)), 0);
    EXPECT_TRUE(hash128_equals(&alternate.id, &original->physicalities[0].id));
    Stage stage(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    ASSERT_EQ(laplace_compose_drain_into_stage(original.get(), stage.get(), &kSourceA,
        10, 1.0, nullptr, 0), 0);
    const size_t entities = intent_stage_entity_count(stage.get());
    ASSERT_EQ(laplace_compose_drain_into_stage(changed.get(), stage.get(), &kSourceB,
        20, 1.0, nullptr, 0), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), entities);
    auto captured = capture(stage);
    ASSERT_NE(captured, nullptr);
    size_t count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(captured.get(), &count);
    ASSERT_EQ(count, original->phys_count * 2u);
    expect_body(bodies[0], original->physicalities[0]);
    expect_body(bodies[original->phys_count], alternate);
    const auto* roots = physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(captured.get()), nullptr);
    EXPECT_FALSE(hash128_equals(&roots[0], &roots[original->phys_count]));
}

TEST_F(GrammarPhysicality, SourceRepresentationRetainsRepeatedAstCompositionWithoutDuplicateEntity) {
    const std::string source = std::string(kGame) + kGame;
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto result = compose(source, ast, true);
    ASSERT_NE(result, nullptr);
    size_t repeated_entities = 0;
    for (size_t i = 0; i < result->entity_count; ++i) {
        for (size_t j = 0; j < i; ++j)
            EXPECT_FALSE(hash128_equals(&result->entities[i].id, &result->entities[j].id));
        size_t physicalities = 0;
        for (size_t j = 0; j < result->phys_count; ++j)
            if (hash128_equals(&result->entities[i].id, &result->physicalities[j].entity_id)) ++physicalities;
        if (physicalities > 1u) ++repeated_entities;
    }
    EXPECT_GT(repeated_entities, 0u) << "The two identical parsed games must retain repeated computed bodies";
    EXPECT_GT(result->phys_count, result->entity_count);
    Stage stage(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceA,
        10, 1.0, nullptr, 0), 0);
    auto captured = capture(stage);
    ASSERT_NE(captured, nullptr);
    size_t count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(captured.get(), &count);
    ASSERT_GE(count, result->phys_count);
    // The grammar drain appends its computed P in order after lexical owners.
    const size_t first = count - result->phys_count;
    for (size_t i = 0; i < result->phys_count; ++i)
        expect_body(bodies[first + i], result->physicalities[i]);
}

TEST_F(GrammarPhysicality, RepeatedParsedSpansRemainResolvableAfterEntityReuse) {
    // Parser coverage only: repeated SAN tokens need not describe a legal game.
    const std::string source = "1. e4 e5 2. e4 e5 *";
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto result = compose(source, ast);
    ASSERT_NE(result, nullptr);
    hash128_t first{};
    size_t occurrences = 0;
    for (size_t i = 0; i < laplace_ast_node_count(ast.get()); ++i) {
        laplace_ast_node_t node{};
        ASSERT_EQ(laplace_ast_get_node(ast.get(), i, &node), 0);
        const char* type = laplace_ast_type_name(ast.get(), node.type_id);
        if (std::strcmp(type, "san_move") != 0 ||
            source.substr(node.start_byte, node.end_byte - node.start_byte) != "e4") continue;
        hash128_t id{};
        ASSERT_EQ(laplace_compose_span_lookup(result.get(), node.start_byte, node.end_byte, &id), 0);
        if (occurrences == 0) first = id;
        else EXPECT_TRUE(hash128_equals(&first, &id));
        ++occurrences;
    }
    ASSERT_EQ(occurrences, 2u);
    size_t entities = 0;
    for (size_t i = 0; i < result->entity_count; ++i)
        if (hash128_equals(&first, &result->entities[i].id)) ++entities;
    EXPECT_EQ(entities, 1u);
}

} // namespace
