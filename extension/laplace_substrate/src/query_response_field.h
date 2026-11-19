#ifndef LAPLACE_QUERY_RESPONSE_FIELD_H
#define LAPLACE_QUERY_RESPONSE_FIELD_H

#include "postgres.h"
#include "laplace/core/hash128.h"

typedef struct RdDefOutRow
{
    hash128_t entity_id;
    char     *label;
    int64     rating_fp;
    int64     witnesses;
} RdDefOutRow;

extern RdDefOutRow *laplace_recall_declared_definitions_c(
    const hash128_t *word,
    int             *out_count);

#endif /* LAPLACE_QUERY_RESPONSE_FIELD_H */
