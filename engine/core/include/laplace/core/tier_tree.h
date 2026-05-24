#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* tier_tree — generic cross-modal tree structure used by every decomposer
 * (text, pixel, audio, code-token, tensor-cell) + the prompt-ingest path
 * (per ADR 0047, ADR 0048, ADR 0040).
 *
 * Topology invariant: nodes are added in TOPOLOGICAL ORDER — every child's
 * index is strictly less than its parent's index. This is the natural
 * bottom-up construction order (codepoints first, then graphemes, then
 * words, ...). The ROOT is the last node added; its parent_idx is
 * TIER_TREE_INVALID.
 *
 * Storage is a single SoA arena with parallel arrays for cache locality.
 * The dominant access patterns:
 *   - hash_composer leaf-to-trunk walk (ADR 0048): iterate indices 0..N-1
 *     in order, computing id[i] = merkle(tier[i], [id[j] for j in children]).
 *   - merkle_dedup top-down walk (ADR 0050 hot loop): iterate indices N-1..0,
 *     skipping subtrees whose root id is already in the substrate.
 *   - tier_tree_get_node random access: one cache miss per field per node.
 * SoA wins for the two walks (sequential per array); AoS would win for the
 * random-access pattern but that pattern is rare relative to the walks.
 *
 * No C++ types cross this header — POD only per RULES R14. C# binds via
 * P/Invoke with SafeHandle over tier_tree_t*. */

typedef struct tier_tree tier_tree_t;

/* Sentinel used for parent_idx (root) and first_child_idx (leaf). */
#define TIER_TREE_INVALID UINT32_MAX

/* Flat node view returned by tier_tree_get_node. POD; suitable for C# P/Invoke
 * marshalling. */
typedef struct {
    uint8_t      tier;            /* 0=atom (codepoint/pixel/...), 1+=composed       */
    uint8_t      _pad[3];         /* explicit padding for 4-byte alignment           */
    uint32_t     parent_idx;      /* index of parent; TIER_TREE_INVALID for root     */
    uint32_t     first_child_idx; /* index of first child; TIER_TREE_INVALID if leaf */
    uint32_t     child_count;     /* count of contiguous children (children are at
                                   * first_child_idx .. first_child_idx+child_count-1) */
    uint32_t     text_range_off;  /* byte offset into source content                 */
    uint32_t     text_range_len;  /* byte length of source range                     */
    uint32_t     atom;            /* leaf-only: atom value (codepoint, pixel, ...)   */
    uint32_t     _pad2;           /* explicit padding to 8-byte alignment for id     */
    hash128_t    id;              /* populated by hash_composer; zero pre-compose    */
    double       coord[4];        /* populated by hash_composer; XYZM                */
    hilbert128_t hilbert;         /* populated by hash_composer                      */
} tier_node_view_t;

/* Allocator + lifecycle. capacity_hint is an upper bound on the number of
 * nodes the caller expects to add; the arena grows past it but at the cost
 * of one or more realloc copies (so callers SHOULD size accurately when
 * possible — e.g. text_decomposer can upper-bound from input byte length).
 * Returns NULL on out-of-memory. */
tier_tree_t* tier_tree_new(size_t capacity_hint);
void         tier_tree_free(tier_tree_t* tree);

/* Counts. */
size_t tier_tree_node_count(const tier_tree_t* tree);
size_t tier_tree_capacity(const tier_tree_t* tree);

/* Add a leaf node (T0 atom — codepoint for text, pixel index for image, etc.).
 * Returns the new node's index, or TIER_TREE_INVALID on allocation failure.
 * Leaf nodes have child_count=0 and first_child_idx=TIER_TREE_INVALID. */
uint32_t tier_tree_add_leaf(
    tier_tree_t* tree,
    uint8_t      tier,
    uint32_t     atom,
    uint32_t     text_range_off,
    uint32_t     text_range_len);

/* Add an interior node referencing a contiguous range of children that have
 * ALREADY been added to the tree. Returns the new node's index, or
 * TIER_TREE_INVALID on allocation failure or invalid child range.
 *
 * Invariant: first_child_idx + child_count <= current_node_count, and every
 * referenced child must have a strictly smaller index than the new node
 * (topological order). Validated; returns TIER_TREE_INVALID on violation. */
uint32_t tier_tree_add_node(
    tier_tree_t* tree,
    uint8_t      tier,
    uint32_t     first_child_idx,
    uint32_t     child_count,
    uint32_t     text_range_off,
    uint32_t     text_range_len);

/* Finalize: walk all interior nodes once to populate parent_idx for every
 * child. MUST be called after all add_* calls and before any consumer reads
 * parent_idx (tier_tree_get_node will return parent_idx=TIER_TREE_INVALID
 * for non-root nodes until finalize runs). Idempotent.
 * Returns 0 on success, non-zero on failure. */
int tier_tree_finalize(tier_tree_t* tree);

/* Read accessor: copy the node's fields into *out. Returns 0 on success,
 * non-zero on out-of-bounds idx or NULL out. */
int tier_tree_get_node(const tier_tree_t* tree, uint32_t idx, tier_node_view_t* out);

/* Mutators used by hash_composer (the only post-construction mutation path).
 * Return 0 on success, non-zero on out-of-bounds idx or NULL input. */
int tier_tree_set_id(tier_tree_t* tree, uint32_t idx, const hash128_t* id);
int tier_tree_set_coord(tier_tree_t* tree, uint32_t idx, const double coord[4]);
int tier_tree_set_hilbert(tier_tree_t* tree, uint32_t idx, const hilbert128_t* hilbert);

/* Bulk SoA accessors for hot-path consumers (hash_composer, merkle_dedup).
 * Returned pointers are valid until the next tier_tree_add_* call or
 * tier_tree_free. They reference the arena directly — zero-copy. */
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
