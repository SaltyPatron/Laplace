#ifndef LAPLACE_SUBSTRATE_QUERY_EVIDENCE_INTERNAL_H
#define LAPLACE_SUBSTRATE_QUERY_EVIDENCE_INTERNAL_H

#include "postgres.h"

#include <limits.h>

#include "catalog/pg_type.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/attestation_engine.h"
#include "laplace/core/hash128.h"

#include "consensus_scan.h"
#include "observation_read.h"
#include "query_evidence.h"
#include "spi_common.h"
#include "walk_score.h"

typedef struct QueryOperandEntry
{
    hash128_t id;
    int first;
} QueryOperandEntry;

typedef struct QueryChannelKey
{
    int32 ordinal;
    uint8 outbound;
    uint8 reserved[3];
    hash128_t candidate;
    hash128_t relation;
} QueryChannelKey;

typedef struct QueryChannelIndex
{
    QueryChannelKey key;
    int heap_index;
} QueryChannelIndex;

typedef struct QueryPlaneKey
{
    int32 ordinal;
    uint8 outbound;
    uint8 reserved[3];
    hash128_t relation;
} QueryPlaneKey;

typedef struct QueryBucket
{
    QueryPlaneKey key;
    LaplaceQueryChannel *heap;
    int count;
    int capacity;
} QueryBucket;

typedef struct QueryScanState
{
    HTAB *operands;
    int *next;
    HTAB *buckets;
    HTAB *channels;
    int fanout;
    bool reverse;
    MemoryContext owner;
} QueryScanState;

typedef struct QueryEvidenceKey
{
    int32 channel_index;
    int32 reserved;
    hash128_t id;
} QueryEvidenceKey;

typedef struct QueryEvidenceWitness
{
    QueryEvidenceKey key;
    hash128_t source;
    hash128_t context;
    bool source_null;
    bool context_null;
    int16 outcome;
    int64 occurrences;
    bool calculation;
} QueryEvidenceWitness;

typedef struct QueryEvidenceState
{
    LaplaceQueryChannel *channels;
    HTAB *channel_index;
    HTAB *sources;
    HTAB *native_sources;
    HTAB *contexts;
    HTAB *provenance;
    HTAB *witnesses;
    HTAB *calculation_channel_sources;
    HTAB *calculation_contexts;
    HTAB *calculation_provenance;
    LaplaceQueryEvidenceStats *stats;
} QueryEvidenceState;

typedef struct QueryExactState
{
    HTAB *operands;
    int *next;
    LaplaceQueryChannel *channels;
    int count;
    int capacity;
    HTAB *channel_index;
    bool reverse;
    MemoryContext owner;
} QueryExactState;

struct LaplaceQueryState
{
    MemoryContext owner;
    ArrayType *operands;
    ArrayType *types;
    LaplaceQueryChannel *channels;
    uint32 *operand_roles;
    int channel_count;
    int channel_capacity;
    int operand_count;
    int fanout;
};

static inline void
laplace_qe_validate_id_array(ArrayType *array, const char *what)
{
    ArrayIterator iterator;
    Datum value;
    bool isnull;

    if (!array)
        return;
    if (ARR_NDIM(array) > 1 || ARR_ELEMTYPE(array) != BYTEAOID)
        ereport(ERROR, (errmsg("query evidence: %s must be a 1-D bytea array", what)));
    iterator = array_create_iterator(array, 0, NULL);
    while (array_iterate(iterator, &value, &isnull))
    {
        if (!isnull && VARSIZE_ANY_EXHDR(DatumGetByteaPP(value)) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("query evidence: %s ids must be 16 bytes", what)));
    }
    array_free_iterator(iterator);
}

static inline bool
laplace_qe_operand_role_valid(uint32 role)
{
    return role >= LAPLACE_QUERY_OPERAND_OBSERVATION &&
           role <= LAPLACE_QUERY_OPERAND_GEOMETRY;
}

static inline uint32
laplace_qe_state_operand_role(const LaplaceQueryState *state, int32 ordinal)
{
    if (!state || ordinal <= 0 || ordinal > state->operand_count || !state->operand_roles)
        ereport(ERROR, (errmsg("query evidence: operand role ordinal is out of range")));
    return state->operand_roles[ordinal - 1];
}

static inline QueryChannelKey
channel_key(const LaplaceQueryChannel *channel)
{
    QueryChannelKey key;
    MemSet(&key, 0, sizeof(key));
    key.ordinal = channel->ordinal;
    key.outbound = channel->outbound ? 1 : 0;
    key.candidate = channel->candidate;
    key.relation = channel->relation;
    return key;
}

static inline QueryPlaneKey
query_plane_key(int ordinal, const hash128_t *relation, bool outbound)
{
    QueryPlaneKey key;
    MemSet(&key, 0, sizeof(key));
    key.ordinal = ordinal;
    key.outbound = outbound ? 1 : 0;
    key.relation = *relation;
    return key;
}

static inline int
query_channel_rank(const LaplaceQueryChannel *a, const LaplaceQueryChannel *b)
{
    __int128 ar = (__int128) a->rating - 2 * (__int128) a->rd;
    __int128 br = (__int128) b->rating - 2 * (__int128) b->rd;
    int cmp;

    if (ar != br)
        return ar > br ? -1 : 1;
    cmp = memcmp(&a->candidate, &b->candidate, sizeof(hash128_t));
    if (cmp != 0)
        return cmp;
    cmp = memcmp(&a->relation, &b->relation, sizeof(hash128_t));
    if (cmp != 0)
        return cmp;
    if (a->outbound != b->outbound)
        return a->outbound ? -1 : 1;
    return 0;
}

static inline int
query_channel_order(const void *left, const void *right)
{
    const LaplaceQueryChannel *a = (const LaplaceQueryChannel *) left;
    const LaplaceQueryChannel *b = (const LaplaceQueryChannel *) right;

    if (a->ordinal != b->ordinal)
        return a->ordinal < b->ordinal ? -1 : 1;
    return query_channel_rank(a, b);
}

/* query_evidence_observations.c */
void laplace_qe_bind_channel_observations(ArrayType *operands,
                                          LaplaceQueryChannel *channels,
                                          int channel_count,
                                          LaplaceQueryEvidenceStats *stats,
                                          MemoryContext work);

#endif
