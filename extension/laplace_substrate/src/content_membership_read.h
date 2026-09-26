#ifndef LAPLACE_CONTENT_MEMBERSHIP_READ_H
#define LAPLACE_CONTENT_MEMBERSHIP_READ_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

/* Receives each matching physicality row: physicality id, owning entity, and
 * trajectory geometry. A match is set membership of constituent ids, not order
 * or adjacency. Datums are valid only during the callback. */
typedef void (*LaplaceContentMembershipConsumer)(Datum physicality, Datum entity,
                                                Datum geometry, void *context);
/* Occurrence read: physicalities of one type whose trajectory constituents
 * overlap members (contain all of them with require_all), through that type's
 * GIN index. Returns false when more than max_rows rows match (0 = unbounded);
 * a bounded read is a prefix of the containers, not all of them. */
bool laplace_typed_membership_read(ArrayType *members, bool require_all,
    int16 physicality_type, uint64 max_rows,
    LaplaceContentMembershipConsumer consume, void *context);
/* Additionally requires containment of required_members, applied in the same
 * GIN scan before max_rows is counted. NULL or empty adds no constraint. */
bool laplace_typed_membership_read_with_required(ArrayType *members, bool require_all,
    ArrayType *required_members, int16 physicality_type, uint64 max_rows,
    LaplaceContentMembershipConsumer consume, void *context);
/* Unbounded read over content trajectories (physicality type 1). */
void laplace_content_membership_read(ArrayType *members, bool require_all,
    LaplaceContentMembershipConsumer consume, void *context);
/* Every distinct entity whose content trajectory matches, in byte order. */
hash128_t *laplace_content_membership_entities(ArrayType *members, bool require_all,
                                              int *count);
#endif
