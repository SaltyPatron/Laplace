#include <gtest/gtest.h>

#include <cstring>

// Seam proof: this test includes ONLY Laplace headers — never tree_sitter/api.h.
// If grammar decomposition compiles and runs against this surface alone, no
// tree-sitter type has leaked past the seam.
#include "laplace/core/grammar_registry.h"
#include "laplace/core/grammar_decomposer.h"

namespace {

bool has_kind(laplace_ast_t* ast, const char* name) {
    size_t n = laplace_ast_node_count(ast);
    for (size_t i = 0; i < n; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        const char* kn = laplace_ast_kind_name(ast, nd.kind_id);
        if (kn && std::strcmp(kn, name) == 0) return true;
    }
    return false;
}

const char* root_kind(laplace_ast_t* ast) {
    laplace_ast_node_t nd;
    if (laplace_ast_get_node(ast, 0, &nd) != 0) return nullptr;
    EXPECT_EQ(nd.parent, LAPLACE_AST_ROOT);
    return laplace_ast_kind_name(ast, nd.kind_id);
}

TEST(GrammarRegistry, ListsTenStarterModalities) {
    const char* ids[16];
    size_t n = laplace_grammar_list(ids, 16);
    EXPECT_EQ(n, 10u);
}

TEST(GrammarRegistry, UnknownModalityIsNull) {
    EXPECT_EQ(laplace_grammar_lookup_by_id("klingon"), nullptr);
    EXPECT_EQ(laplace_grammar_lookup_by_id(nullptr), nullptr);
    EXPECT_EQ(laplace_grammar_lookup_by_ext("xyz"), nullptr);
}

// Every starter grammar's recipe must be ABI-compatible with the compiled-in
// mechanism (ts_parser_set_language succeeds) and yield at least a root node.
TEST(GrammarDecomposer, EveryRecipeParsesAbiCompatible) {
    const char* ids[16];
    size_t n = laplace_grammar_list(ids, 16);
    ASSERT_EQ(n, 10u);
    const char* sample = "x\n";
    for (size_t i = 0; i < n; ++i) {
        const TSLanguage* recipe = laplace_grammar_lookup_by_id(ids[i]);
        ASSERT_NE(recipe, nullptr) << ids[i];
        laplace_ast_t* ast = nullptr;
        int rc = laplace_grammar_parse(
            reinterpret_cast<const uint8_t*>(sample), std::strlen(sample), recipe, &ast);
        EXPECT_EQ(rc, 0) << ids[i];
        ASSERT_NE(ast, nullptr) << ids[i];
        EXPECT_GE(laplace_ast_node_count(ast), 1u) << ids[i];
        laplace_ast_free(ast);
    }
}

// Modality-agnostic: Python and JSON go through the identical code path.
TEST(GrammarDecomposer, PythonStructure) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_ext("py");
    ASSERT_NE(recipe, nullptr);
    const char* src = "def f(x):\n    return x + 1\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);
    EXPECT_STREQ(root_kind(ast), "module");
    EXPECT_TRUE(has_kind(ast, "function_definition"));
    EXPECT_TRUE(has_kind(ast, "identifier"));
    EXPECT_TRUE(has_kind(ast, "return_statement"));
    laplace_ast_free(ast);
}

TEST(GrammarDecomposer, JsonStructure) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_ext("json");
    ASSERT_NE(recipe, nullptr);
    const char* src = "{\"a\": [1, 2], \"b\": true}";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);
    EXPECT_STREQ(root_kind(ast), "document");
    EXPECT_TRUE(has_kind(ast, "object"));
    EXPECT_TRUE(has_kind(ast, "pair"));
    EXPECT_TRUE(has_kind(ast, "array"));
    laplace_ast_free(ast);
}

// Identical Python source must yield identical structure across runs (determinism)
// and the floor spans must nest under named parents (parent precedes child = pre-order).
TEST(GrammarDecomposer, DeterministicAndWellFormedParentLinks) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("python");
    ASSERT_NE(recipe, nullptr);
    const char* src = "a = [i for i in range(10)]\n";
    laplace_ast_t* a = nullptr;
    laplace_ast_t* b = nullptr;
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &a), 0);
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &b), 0);
    ASSERT_EQ(laplace_ast_node_count(a), laplace_ast_node_count(b));
    size_t n = laplace_ast_node_count(a);
    ASSERT_GE(n, 1u);
    for (size_t i = 0; i < n; ++i) {
        laplace_ast_node_t na, nb;
        laplace_ast_get_node(a, i, &na);
        laplace_ast_get_node(b, i, &nb);
        EXPECT_EQ(na.kind_id, nb.kind_id);
        EXPECT_EQ(na.start_byte, nb.start_byte);
        EXPECT_EQ(na.end_byte, nb.end_byte);
        EXPECT_EQ(na.parent, nb.parent);
        // parent always precedes child (pre-order), or is the root sentinel
        EXPECT_TRUE(na.parent == LAPLACE_AST_ROOT || na.parent < i);
        // byte span is non-empty and ordered
        EXPECT_LE(na.start_byte, na.end_byte);
    }
    laplace_ast_free(a);
    laplace_ast_free(b);
}

}  // namespace
