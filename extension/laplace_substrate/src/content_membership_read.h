#ifndef LAPLACE_CONTENT_MEMBERSHIP_READ_H
#define LAPLACE_CONTENT_MEMBERSHIP_READ_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

/* Membership is a set filter, not an ordinal/gap predicate. Datum storage is
 * valid only during the callback. Consumers retain their own selected data. */
typedef void (*LaplaceContentMembershipConsumer)(Datum physicality, Datum entity,
                                                Datum geometry, void *context);
void laplace_content_membership_read(ArrayType *members, bool require_all,
    LaplaceContentMembershipConsumer consume, void *context);
/* Complete, distinct identities in byte order, shared by SQL and native callers. */
hash128_t *laplace_content_membership_entities(ArrayType *members, bool require_all,
                                              int *count);
#endif
