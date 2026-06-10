#include "laplace/core/grammar_decomposer.h"

#include <stdlib.h>

#include "tree_sitter/api.h"

/* tree-sitter is confined to this translation unit (the seam). Everything it
 * produces is immediately turned into Laplace-owned data; no TS type escapes. */

struct laplace_ast {
    laplace_ast_node_t* nodes;
    size_t              count;
    size_t              cap;
    const TSLanguage*   lang;
    int                 oom;
};

static uint32_t ast_append(laplace_ast_t* ast, uint32_t type_id,
                           uint32_t start_byte, uint32_t end_byte,
                           uint32_t parent, uint8_t is_error) {
    if (ast->count >= ast->cap) {
        size_t ncap = ast->cap ? ast->cap * 2 : 256;
        laplace_ast_node_t* n =
            (laplace_ast_node_t*)realloc(ast->nodes, ncap * sizeof(*n));
        if (!n) { ast->oom = 1; return LAPLACE_AST_ROOT; }
        ast->nodes = n;
        ast->cap   = ncap;
    }
    uint32_t idx = (uint32_t)ast->count++;
    laplace_ast_node_t* nd = &ast->nodes[idx];
    nd->type_id    = type_id;
    nd->start_byte = start_byte;
    nd->end_byte   = end_byte;
    nd->parent     = parent;
    nd->is_error   = is_error;
    nd->_pad[0] = nd->_pad[1] = nd->_pad[2] = 0;
    return idx;
}

/* Pre-order: a named node is appended, then its children recurse with it as parent.
 * Anonymous LEAF tokens (operators, punctuation, keywords-as-literals) are appended
 * too — constituency must carry the complete token stream or rendered/generated code
 * loses its syntax. Anonymous interior nodes stay transparent: their descendants
 * link through to the nearest appended ancestor. */
static void ast_walk(laplace_ast_t* ast, TSNode node, uint32_t parent_idx) {
    if (ast->oom) return;
    uint32_t next_parent = parent_idx;
    if (ts_node_is_named(node) || ts_node_child_count(node) == 0) {
        uint32_t idx = ast_append(ast,
            (uint32_t)ts_node_symbol(node),
            ts_node_start_byte(node),
            ts_node_end_byte(node),
            parent_idx,
            ts_node_has_error(node) ? 1u : 0u);
        if (ast->oom) return;
        next_parent = idx;
    }
    uint32_t n = ts_node_child_count(node);
    for (uint32_t i = 0; i < n; ++i)
        ast_walk(ast, ts_node_child(node, i), next_parent);
}

int laplace_grammar_parse(const uint8_t* utf8, size_t len,
                          const TSLanguage* recipe, laplace_ast_t** out_ast) {
    if (!utf8 || !recipe || !out_ast) return -1;
    *out_ast = NULL;

    TSParser* parser = ts_parser_new();
    if (!parser) return -3;
    if (!ts_parser_set_language(parser, recipe)) {
        ts_parser_delete(parser);
        return -2; /* recipe compiled against an incompatible mechanism ABI */
    }

    TSTree* tree = ts_parser_parse_string(parser, NULL,
                                          (const char*)utf8, (uint32_t)len);
    if (!tree) { ts_parser_delete(parser); return -3; }

    laplace_ast_t* ast = (laplace_ast_t*)calloc(1, sizeof(*ast));
    if (!ast) { ts_tree_delete(tree); ts_parser_delete(parser); return -3; }
    ast->lang = recipe;

    ast_walk(ast, ts_tree_root_node(tree), LAPLACE_AST_ROOT);

    ts_tree_delete(tree);
    ts_parser_delete(parser);

    if (ast->oom) { laplace_ast_free(ast); return -3; }
    *out_ast = ast;
    return 0;
}

size_t laplace_ast_node_count(const laplace_ast_t* ast) {
    return ast ? ast->count : 0;
}

int laplace_ast_get_node(const laplace_ast_t* ast, size_t idx, laplace_ast_node_t* out) {
    if (!ast || !out || idx >= ast->count) return -1;
    *out = ast->nodes[idx];
    return 0;
}

const char* laplace_ast_type_name(const laplace_ast_t* ast, uint32_t type_id) {
    if (!ast || !ast->lang) return NULL;
    return ts_language_symbol_name(ast->lang, (TSSymbol)type_id);
}

void laplace_ast_free(laplace_ast_t* ast) {
    if (!ast) return;
    free(ast->nodes);
    free(ast);
}
