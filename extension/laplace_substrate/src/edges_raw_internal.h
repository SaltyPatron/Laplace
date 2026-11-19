#ifndef LAPLACE_EDGES_RAW_INTERNAL_H
#define LAPLACE_EDGES_RAW_INTERNAL_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/hash128.h"

typedef struct LaplaceEdgeRawRow
{
    hash128_t subject_id;
    hash128_t type_id;
    hash128_t neighbour_id;
    hash128_t relation_id;
    hash128_t entity;
    int64     rating;
    int64     rd;
    int64     volatility;
    int64     witness_count;
    double    score;
    uint8     dir; /* 0 = 'out', 1 = 'in' */
    bool      type_null;
    bool      neighbour_null;
    bool      relation_null;
} LaplaceEdgeRawRow;

extern LaplaceEdgeRawRow *laplace_edges_raw_batch_c(
    ArrayType  *subjects,
    const char *direction,
    ArrayType  *types,
    bool        has_limit,
    int32       limit,
    bool        include_refuted,
    const char *rank_mode,
    int        *out_count);

#endif /* LAPLACE_EDGES_RAW_INTERNAL_H */
