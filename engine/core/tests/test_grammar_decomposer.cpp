#include <gtest/gtest.h>

#include <cstring>




#include "laplace/core/grammar_registry.h"
#include "laplace/core/grammar_decomposer.h"

namespace {

bool has_node_type(laplace_ast_t* ast, const char* name) {
    size_t n = laplace_ast_node_count(ast);
    for (size_t i = 0; i < n; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        const char* kn = laplace_ast_type_name(ast, nd.type_id);
        if (kn && std::strcmp(kn, name) == 0) return true;
    }
    return false;
}

const char* root_node_type(laplace_ast_t* ast) {
    laplace_ast_node_t nd;
    if (laplace_ast_get_node(ast, 0, &nd) != 0) return nullptr;
    EXPECT_EQ(nd.parent, LAPLACE_AST_ROOT);
    return laplace_ast_type_name(ast, nd.type_id);
}

TEST(GrammarRegistry, ListsRegisteredModalities) {
    const char* ids[64];
    size_t n = laplace_grammar_list(ids, 64);
    EXPECT_GE(n, 11u);
    bool has_tsv = false, has_csv = false;
    for (size_t i = 0; i < n; ++i) {
        if (ids[i] && std::strcmp(ids[i], "tsv") == 0) has_tsv = true;
        if (ids[i] && std::strcmp(ids[i], "csv") == 0) has_csv = true;
    }
    EXPECT_TRUE(has_tsv);
    EXPECT_TRUE(has_csv);
}

TEST(GrammarRegistry, UnknownModalityIsNull) {
    EXPECT_EQ(laplace_grammar_lookup_by_id("klingon"), nullptr);
    EXPECT_EQ(laplace_grammar_lookup_by_id(nullptr), nullptr);
    EXPECT_EQ(laplace_grammar_lookup_by_ext("xyz"), nullptr);
}



TEST(GrammarDecomposer, TsvStructure) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("tsv");
    ASSERT_NE(recipe, nullptr);
    const char* src = "uri\trel\tstart\tend\tmeta\n/c/en/dog\tRelatedTo\t/c/en/animal\t/c/en/pet\t{\"weight\":1.0}\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);
    EXPECT_STREQ(root_node_type(ast), "document");
    EXPECT_TRUE(has_node_type(ast, "row"));
    EXPECT_TRUE(has_node_type(ast, "field"));
    laplace_ast_free(ast);
}

TEST(GrammarDecomposer, CsvStructure) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("csv");
    ASSERT_NE(recipe, nullptr);
    const char* src = "id,lang,text\n1,en,hello\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);
    EXPECT_STREQ(root_node_type(ast), "document");
    EXPECT_TRUE(has_node_type(ast, "row"));
    EXPECT_TRUE(has_node_type(ast, "field"));
    laplace_ast_free(ast);
}


TEST(GrammarDecomposer, PythonStructure) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_ext("py");
    ASSERT_NE(recipe, nullptr);
    const char* src = "def f(x):\n    return x + 1\n";
    laplace_ast_t* ast = nullptr;
    ASSERT_EQ(laplace_grammar_parse(
        reinterpret_cast<const uint8_t*>(src), std::strlen(src), recipe, &ast), 0);
    ASSERT_NE(ast, nullptr);
    EXPECT_STREQ(root_node_type(ast), "module");
    EXPECT_TRUE(has_node_type(ast, "function_definition"));
    EXPECT_TRUE(has_node_type(ast, "identifier"));
    EXPECT_TRUE(has_node_type(ast, "return_statement"));
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
    EXPECT_STREQ(root_node_type(ast), "document");
    EXPECT_TRUE(has_node_type(ast, "object"));
    EXPECT_TRUE(has_node_type(ast, "pair"));
    EXPECT_TRUE(has_node_type(ast, "array"));
    laplace_ast_free(ast);
}



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
        EXPECT_EQ(na.type_id, nb.type_id);
        EXPECT_EQ(na.start_byte, nb.start_byte);
        EXPECT_EQ(na.end_byte, nb.end_byte);
        EXPECT_EQ(na.parent, nb.parent);
        
        EXPECT_TRUE(na.parent == LAPLACE_AST_ROOT || na.parent < i);
        
        EXPECT_LE(na.start_byte, na.end_byte);
    }
    laplace_ast_free(a);
    laplace_ast_free(b);
}

}  
