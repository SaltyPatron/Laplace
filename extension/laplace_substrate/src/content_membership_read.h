#ifndef LAPLACE_CONTENT_MEMBERSHIP_READ_H
#define LAPLACE_CONTENT_MEMBERSHIP_READ_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

/* Membership is a set filter, not an ordinal/gap predicate. Datum storage is
 * valid only during the callback. Consumers retain their own selected data. */
typedef void (*LaplaceContentMembershipConsumer)(Datum physicality, Datum entity,
                                                Datum geometry, void *context);
/* The same indexed operator with an explicit physicality domain and work
 * envelope. Returns false when a matching row exceeds max_rows (0 = unbounded).
 * A bounded prefix is candidate evidence, never a complete interpretation. */
bool laplace_typed_membership_read(ArrayType *members, bool require_all,
    int16 physicality_type, uint64 max_rows,
    LaplaceContentMembershipConsumer consume, void *context);
void laplace_content_membership_read(ArrayType *members, bool require_all,
    LaplaceContentMembershipConsumer consume, void *context);
/* Complete, distinct identities in byte order, shared by SQL and native callers. */
hash128_t *laplace_content_membership_entities(ArrayType *members, bool require_all,
                                              int *count);
#endif
