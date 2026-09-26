#include "laplace/core/merkle_dedup.h"

#include <stdlib.h>
#include <string.h>

static inline int bitmap_get(const uint8_t* bm, size_t idx) {
    return (bm[idx >> 3] >> (idx & 7u)) & 1u;
}

int merkle_dedup_filter_novel(
    const hash128_t* candidates,
    size_t           n,
    const uint8_t*   existing_bitmap,
    size_t           bitmap_bits,
    hash128_t*       out_novel,
    size_t*          out_n) {
    if (!out_n) return -1;
    if (n == 0) { *out_n = 0; return 0; }
    if (!candidates || !existing_bitmap || !out_novel) return -1;
    if (bitmap_bits < n) return -1;

    size_t out_count = 0;
    for (size_t i = 0; i < n; ++i) {
        if (!bitmap_get(existing_bitmap, i)) {
            out_novel[out_count++] = candidates[i];
        }
    }
    *out_n = out_count;
    return 0;
}

/*
 * Trunk-first presence. Equal content is one entity with one composition
 * physicality, so a node proven present has its whole subtree present: every
 * writer lands a tree leaf to trunk (a tier's physicalities and entities commit
 * before the next tier's), so a stored parent implies stored children. A node is
 * novel only when neither it nor any ancestor is present.
 */
int merkle_dedup_trunk_shortcircuit(
    const tier_tree_t* tree,
    const uint8_t*     existing_bitmap,
    size_t             bitmap_bits,
    uint32_t*          out_novel_indices,
    size_t*            out_n) {
    if (!out_n) return -1;
    if (!tree) return -1;
    const size_t count = tier_tree_node_count(tree);
    if (count == 0) { *out_n = 0; return 0; }
    if (!existing_bitmap || !out_novel_indices) return -1;
    if (bitmap_bits < count) return -1;

    /* 0 unresolved, 1 covered by a present node, 2 novel. */
    uint8_t* state = (uint8_t*)calloc(count, 1);
    uint32_t* path = (uint32_t*)malloc(count * sizeof(uint32_t));
    if (!state || !path) { free(state); free(path); return -1; }
    for (size_t i = 0; i < count; ++i) {
        if (state[i]) continue;
        size_t depth = 0;
        uint32_t at = (uint32_t)i;
        uint8_t verdict = 2;
        while (at != TIER_TREE_INVALID && at < count) {
            if (state[at]) { verdict = state[at]; break; }
            if (bitmap_get(existing_bitmap, at)) { verdict = 1; state[at] = 1; break; }
            path[depth++] = at;
            tier_node_view_t node;
            if (tier_tree_get_node(tree, at, &node) != 0) break;
            at = node.parent_idx;
        }
        while (depth > 0) state[path[--depth]] = verdict;
    }
    size_t out_count = 0;
    for (size_t i = 0; i < count; ++i)
        if (state[i] == 2) out_novel_indices[out_count++] = (uint32_t)i;
    free(state);
    free(path);
    *out_n = out_count;
    return 0;
}
