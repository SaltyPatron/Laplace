#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/* hash_composer — pure leaf-to-trunk content-addressing primitive per
 * ADR 0048. Consumes a topology-only TierTree (produced by TextDecomposer
 * per ADR 0047, or any analogous per-modality decomposer) and populates
 * every node with `id` (BLAKE3-128), `coord` (4D), and `hilbert_index`.
 *
 * Walk is bottom-up by node index — guaranteed correct because tier_tree's
 * add invariant places children at strictly smaller indices than parents
 * (topological order). Single pass; O(N + sum_of_child_counts) ≤ O(2N).
 *
 * T0 atoms (leaves) resolve their (id, coord, hilbert) via a caller-
 * supplied resolver callback. For text the production resolver wraps
 * `codepoint_table_lookup`; for tests + non-text modalities the resolver
 * is whatever fits the modality's atom-table layout.
 *
 * T≥1 interior nodes compose:
 *   id      = hash128_merkle(tier, [child_id...])
 *   coord   = math4d_centroid([child_coord...])
 *   hilbert = hilbert4d_encode(coord)
 *
 * Zero DB. Zero global state (modulo whatever the caller's resolver does).
 * Deterministic by construction per RULES R7 — same TierTree topology
 * plus same resolver outputs → byte-identical populated tree. */

/* Resolver callback. Called once per leaf (tier_tree_first_child_idx ==
 * TIER_TREE_INVALID). Receives the leaf's atom value (codepoint for
 * text, pixel index for image, etc.); must populate *out_id, out_coord[4],
 * *out_hilbert. Returns 0 on success, non-zero on miss / failure.
 *
 * `user_data` is the opaque pointer passed to hash_composer_run; the
 * resolver may use it to carry an atom table pointer, a test stub state,
 * etc. */
typedef int (*hash_composer_atom_resolver_fn)(
    uint32_t      atom,
    void*         user_data,
    hash128_t*    out_id,
    double        out_coord[4],
    hilbert128_t* out_hilbert);

/* Populate id/coord/hilbert for every node in `tree`. Tree must have been
 * built via tier_tree_add_leaf + tier_tree_add_node (topological order).
 * tier_tree_finalize need not have been called — hash_composer does not
 * read parent_idx.
 *
 * Returns 0 on success, non-zero on:
 *   - invalid args (NULL tree or resolver)
 *   - any resolver call returning non-zero (the failed leaf's index is
 *     not currently surfaced; future versions may add a *out_failed_idx
 *     parameter without breaking existing callers via a variant ABI)
 *   - interior node referencing a child range that's out of bounds
 *     (shouldn't happen for a well-formed tier_tree, but defensively
 *     checked) */
int hash_composer_run(
    tier_tree_t*                   tree,
    hash_composer_atom_resolver_fn resolver,
    void*                          resolver_user_data);

#ifdef __cplusplus
}
#endif
