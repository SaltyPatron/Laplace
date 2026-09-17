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
/* Same physical writer, authoritative desired values rather than OR deltas.
 * All-zero desired masks clear storage to NULL; unchanged rows are not written. */
int64 laplace_entity_masks_replace(const LaplaceEntityMaskDelta *masks, int count);
/* Capture and lock every existing physical target before recomputation.
 * The callback fills the masks under its own fresh READ COMMITTED snapshot;
 * replacement then touches only those captured rows, never later creations. */
typedef void (*LaplaceEntityMaskRefresh)(void *context);
int64 laplace_entity_masks_refresh(const LaplaceEntityMaskDelta *masks, int count,
                                  LaplaceEntityMaskRefresh recompute, void *context);
#endif
