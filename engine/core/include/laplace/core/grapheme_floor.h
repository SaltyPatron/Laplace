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

/* Owned-handle variant for cross-language consumers (the floor struct holds heap
 * pointers, awkward to marshal). Heap-allocates and owns the floor; the caller owns
 * the returned tree (free via tier_tree_free) and the floor (free via
 * laplace_grapheme_floor_free_owned). Returns NULL on bad args / invalid UTF-8 / OOM. */
laplace_grapheme_floor_t* laplace_grapheme_floor_build_owned(
    const uint8_t* utf8, size_t len, tier_tree_t** out_tree);

size_t          laplace_grapheme_floor_cp_n(const laplace_grapheme_floor_t* f);
size_t          laplace_grapheme_floor_graph_first_idx(const laplace_grapheme_floor_t* f);
size_t          laplace_grapheme_floor_graph_count(const laplace_grapheme_floor_t* f);
const uint32_t* laplace_grapheme_floor_leaf_text_off(const laplace_grapheme_floor_t* f);
const uint32_t* laplace_grapheme_floor_leaf_text_len(const laplace_grapheme_floor_t* f);
const uint32_t* laplace_grapheme_floor_cp_to_graph(const laplace_grapheme_floor_t* f);

void laplace_grapheme_floor_free_owned(laplace_grapheme_floor_t* f);

/* Maps half-open byte span [start_byte, end_byte) to half-open grapheme ordinals
 * [out_g_start, out_g_end). Returns 0 on success, -1 bad args / empty span. */
int laplace_grapheme_floor_span_to_graphemes(
    const laplace_grapheme_floor_t* f,
    uint32_t start_byte, uint32_t end_byte,
    size_t* out_g_start, size_t* out_g_end);

#ifdef __cplusplus
}
#endif
