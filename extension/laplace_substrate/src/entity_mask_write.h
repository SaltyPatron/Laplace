#ifndef LAPLACE_ENTITY_MASK_WRITE_H
#define LAPLACE_ENTITY_MASK_WRITE_H
#include "postgres.h"
#include "laplace/core/highway_table.h"

typedef struct LaplaceEntityMaskDelta
{
    unsigned char id[16];
    laplace_mask256_t mask;
} LaplaceEntityMaskDelta;

/* ORs sorted, distinct per-entity deltas into the highway mask of each stored
 * tier row. One set read yields the row locations; executor updates maintain
 * indexes and constraints. */
int64 laplace_entity_masks_apply(const LaplaceEntityMaskDelta *deltas, int count);
/* Writes desired masks instead of OR deltas. An all-zero mask stores NULL;
 * unchanged rows are not written. */
int64 laplace_entity_masks_replace(const LaplaceEntityMaskDelta *masks, int count);
/* Locks every existing target row, then calls recompute, which fills the masks
 * under its own fresh READ COMMITTED snapshot; replacement touches only the
 * locked rows, never rows created later. */
typedef void (*LaplaceEntityMaskRefresh)(void *context);
int64 laplace_entity_masks_refresh(const LaplaceEntityMaskDelta *masks, int count,
                                  LaplaceEntityMaskRefresh recompute, void *context);
#endif
