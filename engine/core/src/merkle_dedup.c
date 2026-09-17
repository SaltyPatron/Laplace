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
 * ABI-compatible tree-index filter. Presence belongs to the exact node whose
 * bit was supplied. A parent hash identifies its children but does not prove
 * their storage: independently committed COPY lanes may leave a partial tree.
 * Full-tree reuse is still obtained when every required node is positively
 * present; no allocation or parent traversal is needed for this exact filter.
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

    size_t out_count = 0;
    for (size_t i = 0; i < count; ++i) {
        if (!bitmap_get(existing_bitmap, i))
            out_novel_indices[out_count++] = (uint32_t)i;
    }
    *out_n = out_count;
    return 0;
}
