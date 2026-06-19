#include "laplace/core/grammar_decomposer.h"

#include <stdlib.h>

#include "tree_sitter/api.h"




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





typedef struct { TSNode node; uint32_t parent; } ast_walk_item_t;

static void ast_walk(laplace_ast_t* ast, TSNode root) {
    size_t           cap = 256, top = 0;
    ast_walk_item_t* stack = (ast_walk_item_t*)malloc(cap * sizeof(*stack));
    if (!stack) { ast->oom = 1; return; }
    stack[top].node = root;
    stack[top].parent = LAPLACE_AST_ROOT;
    top++;

    while (top > 0) {
        ast_walk_item_t it = stack[--top];
        TSNode          node = it.node;
        uint32_t        parent_idx = it.parent;
        uint32_t        next_parent = parent_idx;

        if (ts_node_is_named(node) || ts_node_child_count(node) == 0) {
            uint32_t idx = ast_append(ast,
                (uint32_t)ts_node_symbol(node),
                ts_node_start_byte(node),
                ts_node_end_byte(node),
                parent_idx,
                ts_node_has_error(node) ? 1u : 0u);
            if (ast->oom) { free(stack); return; }
            next_parent = idx;
        }

        uint32_t n = ts_node_child_count(node);
        if (n > 0) {
            if (top + (size_t)n > cap) {
                size_t ncap = cap;
                while (top + (size_t)n > ncap) ncap *= 2;
                ast_walk_item_t* ns = (ast_walk_item_t*)realloc(stack, ncap * sizeof(*ns));
                if (!ns) { free(stack); ast->oom = 1; return; }
                stack = ns;
                cap = ncap;
            }
            for (uint32_t i = n; i > 0; --i) {
                stack[top].node   = ts_node_child(node, i - 1);
                stack[top].parent = next_parent;
                top++;
            }
        }
    }
    free(stack);
}

typedef struct {
    const TSLanguage* lang;
    TSParser*         parser;
} tls_parser_slot_t;

#if defined(_MSC_VER)
static __declspec(thread) tls_parser_slot_t g_tls_parser = { NULL, NULL };
#else
static _Thread_local tls_parser_slot_t g_tls_parser = { NULL, NULL };
#endif

static TSParser* parser_pool_acquire(const TSLanguage* recipe) {
    if (g_tls_parser.parser && g_tls_parser.lang == recipe)
        return g_tls_parser.parser;
    if (g_tls_parser.parser) {
        ts_parser_delete(g_tls_parser.parser);
        g_tls_parser.parser = NULL;
        g_tls_parser.lang = NULL;
    }
    TSParser* parser = ts_parser_new();
    if (!parser) return NULL;
    if (!ts_parser_set_language(parser, recipe)) {
        ts_parser_delete(parser);
        return NULL;
    }
    g_tls_parser.lang = recipe;
    g_tls_parser.parser = parser;
    return parser;
}

int laplace_grammar_parse_with(TSParser* parser, const uint8_t* utf8, size_t len,
                               const TSLanguage* recipe, laplace_ast_t** out_ast) {
    if (!parser || !utf8 || !recipe || !out_ast) return -1;
    *out_ast = NULL;

    ts_parser_reset(parser);
    TSTree* tree = ts_parser_parse_string(parser, NULL,
                                          (const char*)utf8, (uint32_t)len);
    if (!tree) return -3;

    laplace_ast_t* ast = (laplace_ast_t*)calloc(1, sizeof(*ast));
    if (!ast) { ts_tree_delete(tree); return -3; }
    ast->lang = recipe;

    ast_walk(ast, ts_tree_root_node(tree));

    ts_tree_delete(tree);

    if (ast->oom) { laplace_ast_free(ast); return -3; }
    *out_ast = ast;
    return 0;
}

int laplace_grammar_parse(const uint8_t* utf8, size_t len,
                          const TSLanguage* recipe, laplace_ast_t** out_ast) {
    if (!utf8 || !recipe || !out_ast) return -1;
    TSParser* parser = parser_pool_acquire(recipe);
    if (!parser) return -3;
    return laplace_grammar_parse_with(parser, utf8, len, recipe, out_ast);
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
