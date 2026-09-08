#ifndef LAPLACE_CONTENT_TRAJECTORY_READ_H
#define LAPLACE_CONTENT_TRAJECTORY_READ_H
#include "postgres.h"
#include "utils/array.h"

typedef void (*LaplaceContentTrajectoryConsumer)(Datum physicality, Datum entity,
    Datum geometry, void *context);
/* Read canonical Content physicalities for a batch of entity IDs under MVCC.
 * The geometry datum is valid only during the callback. */
void laplace_content_trajectory_read(ArrayType *entities,
    LaplaceContentTrajectoryConsumer consume, void *context);
#endif
