#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

int merkle_dedup_filter_novel(
    const hash128_t* candidates,
    size_t           n,
    const uint8_t*   existing_bitmap,
    size_t           bitmap_bits,
    hash128_t*       out_novel,
    size_t*          out_n);

int merkle_dedup_trunk_shortcircuit(
    const tier_tree_t* tree,
    const uint8_t*     existing_bitmap,
    size_t             bitmap_bits,
    uint32_t*          out_novel_indices,
    size_t*            out_n);

#ifdef __cplusplus
}
#endif
