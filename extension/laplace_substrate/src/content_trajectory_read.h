#ifndef LAPLACE_CONTENT_TRAJECTORY_READ_H
#define LAPLACE_CONTENT_TRAJECTORY_READ_H
#include "postgres.h"
#include "utils/array.h"

typedef void (*LaplaceContentTrajectoryConsumer)(Datum physicality, Datum entity,
    Datum geometry, void *context);
/* Physicality kind is an exact identity coordinate, not an interchangeable
 * rendering hint. The shared batch reader hydrates only the requested kind. */
void laplace_typed_trajectory_read(ArrayType *entities, int16 physicality_type,
    LaplaceContentTrajectoryConsumer consume, void *context);
/* Read canonical Content physicalities for a batch of entity IDs under MVCC.
 * The geometry datum is valid only during the callback. */
void laplace_content_trajectory_read(ArrayType *entities,
    LaplaceContentTrajectoryConsumer consume, void *context);
/* Same canonical indexed batch with the stored count, never a derived count. */
typedef void (*LaplaceContentCarrierConsumer)(Datum physicality, Datum entity,
    int32 n_constituents, Datum geometry, void *context);
void laplace_content_carrier_read(ArrayType *entities,
    LaplaceContentCarrierConsumer consume, void *context);
#endif
