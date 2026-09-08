#ifndef LAPLACE_ENTITY_MASK_WRITE_H
#define LAPLACE_ENTITY_MASK_WRITE_H
#include "postgres.h"
#include "laplace/core/highway_table.h"

typedef struct LaplaceEntityMaskDelta
{
    unsigned char id[16];
    laplace_mask256_t mask;
} LaplaceEntityMaskDelta;

/* Sorted, distinct entity deltas. One set read obtains physical tuple locations;
 * PostgreSQL tuple locking, constraints and index maintenance own persistence. */
int64 laplace_entity_masks_apply(const LaplaceEntityMaskDelta *deltas, int count);
#endif
