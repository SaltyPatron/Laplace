#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/* merkle_dedup — SubstrateCRUD hot-loop primitives.
 *
 *: every decomposer routes through one shared write surface.
 * The write surface's hot path is:
 *   1. Send the full ID list to a PG SRF that returns a packed
 *      existing-bitmap (bit i set iff candidate[i].id is already in
 *      laplace.entities).
 *   2. Compact the candidate list into a "novel only" list (the rows we
 *      actually need to send via COPY BINARY).
 *   3. Materialize the COPY buffer via intent_stage and stream it.
 *
 * Step 2 is `merkle_dedup_filter_novel`. For tree-shaped input where most
 * of the tree may already be in substrate (re-ingest of a known document,
 * shared subtree across decomposers), `merkle_dedup_trunk_shortcircuit`
 * additionally prunes subtrees whose root is already present — relying on
 * the framework invariant that SubstrateCRUD-routed intents emit parent
 * and all named descendants atomically, so "parent exists" implies
 * "descendants exist" for any parent that was inserted via this layer.
 *
 * Bitmap convention: packed little-endian-within-byte. Bit i is at
 * `(existing_bitmap[i >> 3] >> (i & 7)) & 1u`. The caller is responsible
 * for ensuring `bitmap_bits >= n` (or `>= node_count(tree)`).
 *
 * Both functions are scalar-first with auto-vectorization-friendly inner
 * loops; an explicit AVX2/AVX-512 SIMD path can be added later without
 * breaking the C ABI. POD only at the boundary. */

/* Filter a candidate ID array into novel-only output via the existing-bitmap.
 *
 *   candidates       — n hashes
 *   n                — number of candidates
 *   existing_bitmap  — packed bitmap; n bits, LSB-first within each byte
 *   bitmap_bits      — capacity of existing_bitmap in bits; must be >= n
 *   out_novel        — caller-allocated buffer with capacity >= n
 *   out_n            — receives count of novel hashes written
 *
 * Returns 0 on success, non-zero on invalid args. Stable order: novel
 * hashes appear in their original candidate-array order. */
int merkle_dedup_filter_novel(
    const hash128_t* candidates,
    size_t           n,
    const uint8_t*   existing_bitmap,
    size_t           bitmap_bits,
    hash128_t*       out_novel,
    size_t*          out_n);

/* Trunk shortcircuit: emit indices of tree nodes that are novel (not in
 * substrate per existing_bitmap) AND whose ancestor chain does not yet
 * have an existing-substrate entry that subsumes them.
 *
 * Algorithm:
 *   - For each node in TOP-DOWN order (highest idx first; root is at
 *     count-1 by tier_tree's topological add invariant), compute skip[i]
 *     = (bit i set) OR (parent_idx[i] != INVALID AND skip[parent_idx[i]]).
 *   - Emit indices where skip[i] is false, in bottom-up order
 *     (children before parents) — matching the natural insertion order
 *     for downstream intent_stage_add_entity calls.
 *
 * Inputs:
 *   tree             — finalized tier_tree (parent_idx populated)
 *   existing_bitmap  — bitmap over node indices (bit i = node i's id is in substrate)
 *   bitmap_bits      — capacity; must be >= tier_tree_node_count(tree)
 *   out_novel_indices— caller-allocated; capacity >= node_count
 *   out_n            — receives count of indices written
 *
 * Invariant assumed: "parent in substrate ⇒ all named descendants in
 * substrate". Holds naturally for trees produced by SubstrateCRUD-routed
 * intents (atomic parent+descendants insertion). Callers operating
 * outside that contract should use `merkle_dedup_filter_novel` directly
 * over the tree's id array.
 *
 * Returns 0 on success, non-zero on invalid args, OOM, or unfinalized tree. */
int merkle_dedup_trunk_shortcircuit(
    const tier_tree_t* tree,
    const uint8_t*     existing_bitmap,
    size_t             bitmap_bits,
    uint32_t*          out_novel_indices,
    size_t*            out_n);

#ifdef __cplusplus
}
#endif
