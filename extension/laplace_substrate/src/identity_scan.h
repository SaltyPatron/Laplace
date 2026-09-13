#ifndef LAPLACE_IDENTITY_SCAN_H
#define LAPLACE_IDENTITY_SCAN_H
#include "postgres.h"
#include "executor/tuptable.h"
#include "utils/array.h"

typedef void (*LaplaceIdentityConsumer)(TupleTableSlot *slot, AttrNumber id,
    void *context);
/* One native primary-key array scan under the active MVCC snapshot. The caller
 * owns relation authorization and partition selection. Returns false, without
 * callbacks, when the leaf does not have the supported bytea identity index. */
bool laplace_identity_scan(Oid leaf, ArrayType *ids,
    LaplaceIdentityConsumer consume, void *context);
#endif
