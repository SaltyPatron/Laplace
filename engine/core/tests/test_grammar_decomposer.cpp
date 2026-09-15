#include <gtest/gtest.h>

#include <cstring>
#include <string>




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

TEST(GrammarDecomposer, CsvRecordOver64KiBKeepsGrammarFraming) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("csv");
    ASSERT_NE(recipe, nullptr);
    laplace_grammar_row_iter_t* iter = nullptr;
    ASSERT_EQ(laplace_grammar_row_iter_new(recipe, &iter), 0);
    ASSERT_NE(iter, nullptr);

    std::string src = "\"" + std::string(70000, 'x') +
                      "\ncontinued\",tail\nnext,row\n";
    laplace_raw_row_t* rows = nullptr;
    size_t count = 0;
    ASSERT_EQ(laplace_grammar_row_iter_feed_lines(
                  iter, reinterpret_cast<const uint8_t*>(src.data()), src.size(),
                  &rows, &count), 0);
    ASSERT_EQ(count, 1u);
    EXPECT_GT(rows[0].row_len, 65536u);
    EXPECT_NE(std::memchr(rows[0].row_utf8, '\n', rows[0].row_len), nullptr);
    laplace_grammar_row_iter_free_lines(rows, count);

    rows = nullptr;
    count = 0;
    ASSERT_EQ(laplace_grammar_row_iter_feed_lines(iter, nullptr, 0,
                                                  &rows, &count), 0);
    ASSERT_EQ(count, 1u);
    laplace_grammar_row_iter_free_lines(rows, count);
    laplace_grammar_row_iter_free(iter);
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

TEST(GrammarDecomposer, DiagnosticsDistinguishErrorsMissingAndPropagatedFlags) {
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("cpp");
    ASSERT_NE(recipe, nullptr);
    struct Example { const char* source; bool complete; bool error; bool missing; };
    const Example examples[] = {
        {"int n = 1;", true, false, false},
        {"int n = @;", false, true, false},
        {"int n = 1", false, false, true},
    };
    for (const auto& example : examples) {
        SCOPED_TRACE(example.source);
        laplace_ast_t* ast = nullptr;
        ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(example.source),
            std::strlen(example.source), recipe, &ast), 0);
        ASSERT_NE(ast, nullptr);
        laplace_ast_diagnostics_t diagnostics{};
        ASSERT_EQ(laplace_ast_get_diagnostics(ast, &diagnostics), 0);
        EXPECT_EQ(diagnostics.ast_node_count, laplace_ast_node_count(ast));
        EXPECT_GE(diagnostics.syntax_node_count, diagnostics.ast_node_count);
        EXPECT_EQ(diagnostics.root_has_error == 0, example.complete);
        EXPECT_EQ(diagnostics.error_node_count > 0, example.error);
        EXPECT_EQ(diagnostics.missing_node_count > 0, example.missing);
        EXPECT_EQ(diagnostics.reserved, 0u);
        size_t actual_errors = 0, propagated_errors = 0;
        for (size_t i = 0; i < diagnostics.ast_node_count; ++i) {
            laplace_ast_node_t node{};
            ASSERT_EQ(laplace_ast_get_node(ast, i, &node), 0);
            const char* type = laplace_ast_type_name(ast, node.type_id);
            if (type && std::strcmp(type, "ERROR") == 0) actual_errors++;
            if (node.is_error) propagated_errors++;
        }
        EXPECT_EQ(diagnostics.error_node_count, actual_errors);
        if (example.error) EXPECT_GT(propagated_errors, actual_errors);
        laplace_ast_diagnostics_t repeated{};
        ASSERT_EQ(laplace_ast_get_diagnostics(ast, &repeated), 0);
        EXPECT_EQ(std::memcmp(&diagnostics, &repeated, sizeof(diagnostics)), 0);
        laplace_ast_free(ast);
    }
}

TEST(GrammarDecomposer, DiagnosticsRejectInvalidArguments) {
    laplace_ast_diagnostics_t diagnostics{};
    EXPECT_EQ(laplace_ast_get_diagnostics(nullptr, &diagnostics), -1);
    const TSLanguage* recipe = laplace_grammar_lookup_by_id("cpp");
    ASSERT_NE(recipe, nullptr);
    laplace_ast_t* ast = nullptr;
    const char* source = "int n = 1;";
    ASSERT_EQ(laplace_grammar_parse(reinterpret_cast<const uint8_t*>(source), std::strlen(source), recipe, &ast), 0);
    EXPECT_EQ(laplace_ast_get_diagnostics(ast, nullptr), -1);
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
