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

    const uint32_t* parent = tier_tree_parent_idx_array(tree);
    if (!parent) return -1;

    uint8_t* skip = (uint8_t*)calloc(count, 1);
    if (!skip) return -1;

    for (size_t r = count; r > 0; --r) {
        const size_t i = r - 1;
        const int self_existing = bitmap_get(existing_bitmap, i);
        int parent_skipped = 0;
        if (parent[i] != TIER_TREE_INVALID) {
            parent_skipped = skip[parent[i]];
        }
        skip[i] = (self_existing || parent_skipped) ? (uint8_t)1 : (uint8_t)0;
    }

    size_t out_count = 0;
    for (size_t i = 0; i < count; ++i) {
        if (!skip[i]) {
            out_novel_indices[out_count++] = (uint32_t)i;
        }
    }
    *out_n = out_count;
    free(skip);
    return 0;
}
