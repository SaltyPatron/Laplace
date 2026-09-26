#ifndef LAPLACE_CONTENT_TRAJECTORY_READ_H
#define LAPLACE_CONTENT_TRAJECTORY_READ_H
#include "postgres.h"
#include "utils/array.h"

typedef void (*LaplaceContentTrajectoryConsumer)(Datum physicality, Datum entity,
    Datum geometry, void *context);
/* Reads the trajectory physicality of one type for a batch of entities under
 * the active snapshot. The type is part of the physicality id, so only rows of
 * that type are handed on. The geometry datum is valid only during the callback. */
void laplace_typed_trajectory_read(ArrayType *entities, int16 physicality_type,
    LaplaceContentTrajectoryConsumer consume, void *context);
/* The same read over content trajectories (physicality type 1). */
void laplace_content_trajectory_read(ArrayType *entities,
    LaplaceContentTrajectoryConsumer consume, void *context);
/* The content read that also hands on the stored constituent count. */
typedef void (*LaplaceContentCarrierConsumer)(Datum physicality, Datum entity,
    int32 n_constituents, Datum geometry, void *context);
void laplace_content_carrier_read(ArrayType *entities,
    LaplaceContentCarrierConsumer consume, void *context);
/* Work envelope for the bounded readers: a ceiling on leaf-partition reads
 * (each nonempty leaf counted before it is opened) and on per-frontier scratch
 * bytes, executor and catalog bookkeeping excluded. Scratch is freed before
 * return; callbacks run in the caller's memory context. */
typedef struct LaplaceContentReadBudget {
    int maximum_leaf_reads;
    int leaf_reads;
    size_t maximum_scratch_bytes;
} LaplaceContentReadBudget;
/* Bounded read of required manifests: a probed row of another type or with a
 * NULL trajectory is an error. */
void laplace_typed_carrier_read_bounded(ArrayType *entities, int16 physicality_type,
    LaplaceContentCarrierConsumer consume, void *context,
    LaplaceContentReadBudget *budget);
void laplace_content_carrier_read_bounded(ArrayType *entities,
    LaplaceContentCarrierConsumer consume, void *context,
    LaplaceContentReadBudget *budget);
#endif
