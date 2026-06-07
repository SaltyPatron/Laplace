#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/grammar_registry.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The seam. The grammar-execution mechanism (tree-sitter, compiled in-engine) runs
 * ENTIRELY behind laplace_grammar_parse; nothing past this boundary sees a tree-sitter
 * type. Callers receive a flat, Laplace-owned AST of NAMED nodes in pre-order
 * (parent always precedes its children), which composes into the tier tree the same
 * way text words/sentences do: each node's constituents are an ordered id sequence. */

#define LAPLACE_AST_ROOT UINT32_MAX

typedef struct {
    uint32_t kind_id;     /* recipe-local node-kind id; name via laplace_ast_kind_name */
    uint32_t start_byte;  /* [start_byte, end_byte) byte span in the input */
    uint32_t end_byte;
    uint32_t parent;      /* index of nearest named ancestor, or LAPLACE_AST_ROOT */
    uint8_t  is_error;    /* node's subtree contains an ERROR/MISSING */
    uint8_t  _pad[3];
} laplace_ast_node_t;

typedef struct laplace_ast laplace_ast_t;

/* Parse utf8 with a modality recipe. Returns 0 and *out_ast on success;
 * -1 bad args, -2 recipe/mechanism ABI mismatch (set_language failed), -3 OOM/parse failure.
 * Anonymous nodes (keywords/punctuation) are dropped: their bytes remain in the grapheme
 * floor, but they are not AST entities — named descendants link to the nearest named ancestor. */
int laplace_grammar_parse(const uint8_t* utf8, size_t len,
                          const TSLanguage* recipe, laplace_ast_t** out_ast);

size_t laplace_ast_node_count(const laplace_ast_t* ast);
int    laplace_ast_get_node(const laplace_ast_t* ast, size_t idx, laplace_ast_node_t* out);
const char* laplace_ast_kind_name(const laplace_ast_t* ast, uint32_t kind_id);
void   laplace_ast_free(laplace_ast_t* ast);

#ifdef __cplusplus
}
#endif
