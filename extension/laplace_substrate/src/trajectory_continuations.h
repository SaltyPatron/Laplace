#ifndef LAPLACE_TRAJECTORY_CONTINUATIONS_H
#define LAPLACE_TRAJECTORY_CONTINUATIONS_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

typedef struct LaplaceContinuation
{
    hash128_t id;
    int64 occurrences;
    int stride;
} LaplaceContinuation;

/* Complete successor set, allocated in the caller's memory context. Exact
 * reads and longest-suffix proposal share this indexed native operation. */
LaplaceContinuation *laplace_trajectory_continuations(
    ArrayType *context, bool suffix_backoff, int *count);

#endif
