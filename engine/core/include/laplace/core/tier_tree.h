#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct tier_tree tier_tree_t;

#define TIER_TREE_INVALID UINT32_MAX

typedef struct {
    uint8_t      tier;
    uint8_t      _pad[3];
    uint32_t     parent_idx;
    uint32_t     first_child_idx;
    uint32_t     child_count;
    uint32_t     text_range_off;
    uint32_t     text_range_len;
    uint32_t     atom;
    uint32_t     _pad2;
    hash128_t    id;
    double       coord[4];
    hilbert128_t hilbert;
} tier_node_view_t;

tier_tree_t* tier_tree_new(size_t capacity_hint);
void         tier_tree_free(tier_tree_t* tree);

size_t tier_tree_node_count(const tier_tree_t* tree);
size_t tier_tree_capacity(const tier_tree_t* tree);

uint32_t tier_tree_add_leaf(
    tier_tree_t* tree,
    uint8_t      tier,
    uint32_t     atom,
    uint32_t     text_range_off,
    uint32_t     text_range_len);

uint32_t tier_tree_add_node(
    tier_tree_t* tree,
    uint8_t      tier,
    uint32_t     first_child_idx,
    uint32_t     child_count,
    uint32_t     text_range_off,
    uint32_t     text_range_len);

int tier_tree_finalize(tier_tree_t* tree);

int tier_tree_get_node(const tier_tree_t* tree, uint32_t idx, tier_node_view_t* out);

/* The tree's own text buffer — the EXACT bytes (post-NFC) that every node's
 * text_range_off/len index into. Installed by the decomposer; OWNED and freed
 * by the tree. Consumers must slice THIS buffer, never the caller's original
 * input: offsets are in normalized space, and NFC changes byte positions and
 * lengths (GH #1039: NFD input mis-sliced words mid-UTF-8, and NFC-expanding
 * codepoints like U+0958 read past the caller's allocation). */
int            tier_tree_set_text(tier_tree_t* tree, uint8_t* text_owned, size_t len);
const uint8_t* tier_tree_text(const tier_tree_t* tree, size_t* out_len);

int tier_tree_set_id(tier_tree_t* tree, uint32_t idx, const hash128_t* id);
int tier_tree_set_coord(tier_tree_t* tree, uint32_t idx, const double coord[4]);
int tier_tree_set_hilbert(tier_tree_t* tree, uint32_t idx, const hilbert128_t* hilbert);

int tier_tree_set_parent(tier_tree_t* tree, uint32_t idx, uint32_t parent_idx);

const uint8_t*      tier_tree_tier_array(const tier_tree_t* tree);
const uint32_t*     tier_tree_first_child_idx_array(const tier_tree_t* tree);
const uint32_t*     tier_tree_child_count_array(const tier_tree_t* tree);
const uint32_t*     tier_tree_parent_idx_array(const tier_tree_t* tree);
const uint32_t*     tier_tree_atom_array(const tier_tree_t* tree);
const uint32_t*     tier_tree_text_off_array(const tier_tree_t* tree);
const uint32_t*     tier_tree_text_len_array(const tier_tree_t* tree);
const hash128_t*    tier_tree_id_array(const tier_tree_t* tree);
hash128_t*          tier_tree_id_array_mut(tier_tree_t* tree);
const double*       tier_tree_coord_array(const tier_tree_t* tree);
double*             tier_tree_coord_array_mut(tier_tree_t* tree);
const hilbert128_t* tier_tree_hilbert_array(const tier_tree_t* tree);
hilbert128_t*       tier_tree_hilbert_array_mut(tier_tree_t* tree);

#ifdef __cplusplus
}
#endif
