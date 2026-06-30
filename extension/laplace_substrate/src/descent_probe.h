#pragma once

#include "postgres.h"

#include "utils/array.h"

/* One hash-join probe: set bit i when candidate i exists in laplace.entities. */
int laplace_entities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);

/*
 * Merkle containment descent over flat (id, parent-index) arrays.
 * bm is pre-filled 0xFF (all PRESENT); clears bits for novel nodes only.
 */
void laplace_content_descent_bitmap_core(
    const uint8_t *ids16,
    const int32_t *parents,
    int n,
    uint8_t *bm);
