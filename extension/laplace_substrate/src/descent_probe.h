#pragma once

#include "postgres.h"

#include "utils/array.h"

int laplace_entities_present_bitmap(ArrayType *ids_array, uint8_t *bm, int candidate_count);

void laplace_content_descent_bitmap_core(
    const uint8_t *ids16,
    const int32_t *parents,
    int n,
    uint8_t *bm);
