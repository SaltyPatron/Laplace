#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The shared codepoint->grapheme floor: tier-0 codepoint leaves and tier-1
 * grapheme nodes, identical for every segmentation law above it (UAX#29 text,
 * tree-sitter code, ...). Built once over the input bytes; the law above the
 * floor (words/sentences for text, AST nodes for code) composes from these
 * grapheme entities, so the same surface always yields the same floor and
 * reconciles across laws (a code identifier and a prose word share grapheme ids). */
typedef struct {
    uint32_t* cps;             /* decoded codepoints, length cp_n */
    size_t    cp_n;
    uint32_t* leaf_text_off;   /* per-cp byte offset into the input, length cp_n */
    uint32_t* leaf_text_len;   /* per-cp byte length, length cp_n */
    size_t    graph_first_idx; /* tree index of the first tier-1 grapheme node */
    size_t    graph_count;     /* number of grapheme nodes */
    uint32_t* cp_to_graph;     /* per-cp grapheme ordinal (0..graph_count), length cp_n */
} laplace_grapheme_floor_t;

/* Decodes UTF-8, creates the tier tree (caller owns it via *out_tree), and adds
 * tier-0 codepoint leaves + tier-1 grapheme nodes; fills *out. Assumes len > 0.
 * Returns 0 on success, -1 bad args, -2 invalid UTF-8, -3 allocation failure.
 * On failure *out_tree is NULL and *out is zeroed. On success the caller frees
 * the tree (tier_tree_free) and the floor metadata (laplace_grapheme_floor_free). */
int laplace_grapheme_floor_build(const uint8_t* utf8, size_t len,
                                 tier_tree_t** out_tree,
                                 laplace_grapheme_floor_t* out);

void laplace_grapheme_floor_free(laplace_grapheme_floor_t* f);

#ifdef __cplusplus
}
#endif
