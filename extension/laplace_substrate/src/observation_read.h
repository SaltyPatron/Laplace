#ifndef LAPLACE_OBSERVATION_READ_H
#define LAPLACE_OBSERVATION_READ_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

/* A recorded witness, not a pooled standing or a rendered relation label. */
typedef struct LaplaceObservation
{
    hash128_t id, subject, type, object, source, context;
    bool object_null, source_null, context_null;
    int16 outcome;
    int64 occurrences;
} LaplaceObservation;

/* ordinal is the original 1-based operand occurrence. Duplicate operands share
 * a database probe but retain every occurrence. The callback borrows the row
 * only for its duration. NULL filters mean unrestricted; empty filters mean no
 * witnesses. Rows are a set; ordinal and witness id identify their bindings. */
/* Roles preserve the stored proposition: 1 = subject, 2 = object. An inbound
 * binding does not reverse an asymmetric assertion. A self-reference has both
 * roles, even though its physical witness is fetched only once. */
typedef void (*LaplaceObservationVisitor)(int ordinal, int16 role,
    const LaplaceObservation *observation, void *context);
void laplace_observation_read(ArrayType *operands, ArrayType *sources,
    ArrayType *types, int roles, LaplaceObservationVisitor visitor, void *context);

#endif
