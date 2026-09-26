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

    // The composition's physicalities in first-seen order, one per physicality id.
    static std::vector<const laplace_compose_physicality_t*> distinct(const laplace_compose_result_t& r) {
        std::vector<const laplace_compose_physicality_t*> out;
        for (size_t i = 0; i < r.phys_count; ++i) {
            bool seen = false;
            for (const auto* p : out) seen = seen || hash128_equals(&p->id, &r.physicalities[i].id);
            if (!seen) out.push_back(&r.physicalities[i]);
        }
        return out;
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

// Same content, same physicality: a second source composing the same game stages no
// second entity and no second form. Its provenance is its own trunk's trajectory.
TEST_F(GrammarPhysicality, DistinctSourcesStageEachFormOnce) {
    const std::string source = kGame;
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto result = compose(source, ast);
    ASSERT_NE(result, nullptr);
    ASSERT_GT(result->phys_count, 0u);
    const auto forms = distinct(*result);
    Stage stage(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceA,
        10, 1.0, nullptr, 0), 0);
    const size_t first_entities = intent_stage_entity_count(stage.get());
    ASSERT_GT(first_entities, 0u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), forms.size());
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceB,
        20, 1.0, nullptr, 0), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), first_entities);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), forms.size());
    auto captured = capture(stage);
    ASSERT_NE(captured, nullptr);
    size_t count = 0;
    const auto* inputs = physicality_descriptor_capture_inputs(captured.get(), &count);
    ASSERT_EQ(count, forms.size());
    for (size_t i = 0; i < forms.size(); ++i) expect_body(inputs[i], *forms[i]);
}

// A present composition is its entity and its one composition physicality: a present
// bitmap stages nothing, entity or physicality.
TEST_F(GrammarPhysicality, PresentCompositionsStageNoEntityAndNoPhysicality) {
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
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceA,
        42, 1.0, present.data(), nodes), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 0u);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 0u);
}

// A physicality id is its entity and its type: a second body computed for the same
// placement is the form already staged, not another one.
TEST_F(GrammarPhysicality, AnotherBodyAtTheSamePlacementIsTheStagedForm) {
    const std::string source = kGame;
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto original = compose(source, ast);
    auto changed = compose(source, ast);
    ASSERT_NE(original, nullptr);
    ASSERT_NE(changed, nullptr);
    ASSERT_GT(original->phys_count, 0u);
    auto& alternate = changed->physicalities[0];
    ASSERT_GE(alternate.n_constituents, 2u);
    for (size_t i = 0; i < 4; ++i) alternate.coord[i] += 0.125;
    EXPECT_TRUE(hash128_equals(&alternate.id, &original->physicalities[0].id));
    const auto forms = distinct(*original);
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
    ASSERT_EQ(count, forms.size());
    expect_body(bodies[0], original->physicalities[0]);
}

// Two identical games in one source are one composition occurring twice in the file's
// trajectory: the drain stages each entity and each form once.
TEST_F(GrammarPhysicality, SourceRepresentationStagesRepeatedCompositionOnce) {
    const std::string source = std::string(kGame) + kGame;
    auto ast = parse(source);
    ASSERT_NE(ast, nullptr);
    auto result = compose(source, ast, true);
    ASSERT_NE(result, nullptr);
    for (size_t i = 0; i < result->entity_count; ++i)
        for (size_t j = 0; j < i; ++j)
            EXPECT_FALSE(hash128_equals(&result->entities[i].id, &result->entities[j].id));
    const auto forms = distinct(*result);
    Stage stage(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    ASSERT_EQ(laplace_compose_drain_into_stage(result.get(), stage.get(), &kSourceA,
        10, 1.0, nullptr, 0), 0);
    auto captured = capture(stage);
    ASSERT_NE(captured, nullptr);
    size_t count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(captured.get(), &count);
    ASSERT_GE(count, forms.size());
    for (size_t i = 0; i < count; ++i)
        for (size_t j = 0; j < i; ++j)
            EXPECT_FALSE(hash128_equals(&bodies[i].entity_id, &bodies[j].entity_id)
                && bodies[i].type == bodies[j].type);
    // The grammar drain appends its forms in order after lexical owners.
    const size_t first = count - forms.size();
    for (size_t i = 0; i < forms.size(); ++i) expect_body(bodies[first + i], *forms[i]);
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
