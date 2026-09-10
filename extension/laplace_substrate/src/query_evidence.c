#include "postgres.h"

#include <limits.h>

#include "catalog/pg_type.h"
#include "miscadmin.h"
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

typedef struct QueryBucket
{
    LaplaceQueryChannel *heap;
    int count;
    int capacity;
} QueryBucket;

typedef struct QueryScanState
{
    HTAB *operands;
    int *next;
    QueryBucket *buckets;
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

typedef struct QueryEvidenceState
{
    LaplaceQueryChannel *channels;
    HTAB *channel_index;
    HTAB *sources;
    HTAB *contexts;
    LaplaceQueryEvidenceStats *stats;
} QueryEvidenceState;

static void
validate_id_array(ArrayType *array, const char *what)
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

static QueryChannelKey
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

/* Negative means a is the stronger retained channel.  Candidate generation is
 * bounded by pooled conservative standing only; relation identity/direction
 * remain explicit operands for the later query-relative operator. */
static int
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

static int
query_channel_order(const void *left, const void *right)
{
    const LaplaceQueryChannel *a = (const LaplaceQueryChannel *) left;
    const LaplaceQueryChannel *b = (const LaplaceQueryChannel *) right;

    if (a->ordinal != b->ordinal)
        return a->ordinal < b->ordinal ? -1 : 1;
    return query_channel_rank(a, b);
}

static void
query_heap_store(QueryScanState *state, QueryBucket *bucket, int at,
                 const LaplaceQueryChannel *channel)
{
    QueryChannelKey key = channel_key(channel);
    QueryChannelIndex *entry = (QueryChannelIndex *)
        hash_search(state->channels, &key, HASH_ENTER, NULL);
    entry->heap_index = at;
    bucket->heap[at] = *channel;
}

static void
query_heap_down(QueryScanState *state, QueryBucket *bucket, int at,
                const LaplaceQueryChannel *channel)
{
    while (at < bucket->count / 2)
    {
        int child = at * 2 + 1;
        if (child + 1 < bucket->count &&
            query_channel_rank(&bucket->heap[child + 1], &bucket->heap[child]) > 0)
            ++child;
        if (query_channel_rank(&bucket->heap[child], channel) <= 0)
            break;
        query_heap_store(state, bucket, at, &bucket->heap[child]);
        at = child;
    }
    query_heap_store(state, bucket, at, channel);
}

static void
query_channel_insert(QueryScanState *state, int ordinal,
                     const LaplaceQueryChannel *channel)
{
    QueryBucket *bucket = &state->buckets[ordinal - 1];
    QueryChannelKey key = channel_key(channel);
    QueryChannelIndex *entry = (QueryChannelIndex *)
        hash_search(state->channels, &key, HASH_FIND, NULL);

    if (entry != NULL)
    {
        if (query_channel_rank(channel, &bucket->heap[entry->heap_index]) < 0)
            query_heap_down(state, bucket, entry->heap_index, channel);
        return;
    }

    if (bucket->count == state->fanout)
    {
        if (query_channel_rank(channel, &bucket->heap[0]) >= 0)
            return;
        QueryChannelKey removed = channel_key(&bucket->heap[0]);
        hash_search(state->channels, &removed, HASH_REMOVE, NULL);
        query_heap_down(state, bucket, 0, channel);
        return;
    }

    if (bucket->count == bucket->capacity)
    {
        int64 capacity = Min((int64) state->fanout,
                             bucket->capacity ? (int64) bucket->capacity * 2 : 8);
        MemoryContext previous;

        if ((uint64) capacity > MaxAllocSize / sizeof(LaplaceQueryChannel))
            ereport(ERROR,
                    (errmsg("query evidence: per-occurrence fanout exceeds allocation capacity")));
        previous = MemoryContextSwitchTo(state->owner);
        bucket->heap = bucket->heap
            ? (LaplaceQueryChannel *) repalloc(bucket->heap,
                                               capacity * sizeof(LaplaceQueryChannel))
            : (LaplaceQueryChannel *) palloc(capacity * sizeof(LaplaceQueryChannel));
        MemoryContextSwitchTo(previous);
        bucket->capacity = (int) capacity;
    }

    {
        int at = bucket->count++;
        while (at > 0)
        {
            int parent = (at - 1) / 2;
            if (query_channel_rank(&bucket->heap[parent], channel) >= 0)
                break;
            query_heap_store(state, bucket, at, &bucket->heap[parent]);
            at = parent;
        }
        query_heap_store(state, bucket, at, channel);
    }
}

static void
query_consensus_cell(const LaplaceConsensusRow *row, void *opaque)
{
    QueryScanState *state = (QueryScanState *) opaque;
    const hash128_t *anchor;
    const hash128_t *candidate;
    QueryOperandEntry *operand;

    if (row->object_is_null)
        return;
    if (!(walk_edge_score(row->type, row->rating, row->rd) > 0.0))
        return;

    anchor = state->reverse ? &row->object : &row->subject;
    candidate = state->reverse ? &row->subject : &row->object;
    operand = (QueryOperandEntry *) hash_search(state->operands, anchor, HASH_FIND, NULL);
    if (!operand)
        return;

    for (int index = operand->first; index >= 0; index = state->next[index])
    {
        LaplaceQueryChannel channel;
        MemSet(&channel, 0, sizeof(channel));
        channel.ordinal = index + 1;
        channel.anchor = *anchor;
        channel.candidate = *candidate;
        channel.relation = row->type;
        channel.outbound = !state->reverse;
        channel.rating = row->rating;
        channel.rd = row->rd;
        channel.witnesses = row->witnesses;
        query_channel_insert(state, channel.ordinal, &channel);
    }
}

static bool
add_occurrences(int64 *target, int64 value)
{
    if (value < 0 || *target > PG_INT64_MAX - value)
        return false;
    *target += value;
    return true;
}

static void
query_observation(int ordinal, int16 role,
                  const LaplaceObservation *row, void *opaque)
{
    QueryEvidenceState *state = (QueryEvidenceState *) opaque;
    LaplaceQueryChannel probe;
    QueryChannelKey key;
    QueryChannelIndex *index;
    LaplaceQueryChannel *channel;

    if (row->object_null)
        return;

    MemSet(&probe, 0, sizeof(probe));
    probe.ordinal = ordinal;
    probe.anchor = role == 1 ? row->subject : row->object;
    probe.candidate = role == 1 ? row->object : row->subject;
    probe.relation = row->type;
    probe.outbound = role == 1;
    key = channel_key(&probe);
    index = (QueryChannelIndex *) hash_search(state->channel_index, &key, HASH_FIND, NULL);
    if (!index)
        return;

    channel = &state->channels[index->heap_index];
    if (channel->observation_rows == INT_MAX)
        ereport(ERROR, (errmsg("query evidence: observation row count overflow")));
    channel->observation_rows++;
    if (!add_occurrences(&channel->observation_occurrences, row->occurrences))
        ereport(ERROR, (errmsg("query evidence: observation occurrence count overflow")));

    switch (row->outcome)
    {
        case LAPLACE_ATTESTATION_OUTCOME_CONFIRM:
            if (!add_occurrences(&channel->confirm_occurrences, row->occurrences))
                ereport(ERROR, (errmsg("query evidence: confirmation count overflow")));
            break;
        case LAPLACE_ATTESTATION_OUTCOME_DRAW:
            if (!add_occurrences(&channel->draw_occurrences, row->occurrences))
                ereport(ERROR, (errmsg("query evidence: draw count overflow")));
            break;
        case LAPLACE_ATTESTATION_OUTCOME_REFUTE:
            if (!add_occurrences(&channel->refute_occurrences, row->occurrences))
                ereport(ERROR, (errmsg("query evidence: refutation count overflow")));
            break;
        default:
            ereport(ERROR,
                    (errmsg("query evidence: invalid attestation outcome %d", row->outcome)));
    }

    if (!row->source_null)
    {
        QueryEvidenceKey source;
        bool found;
        MemSet(&source, 0, sizeof(source));
        source.channel_index = index->heap_index;
        source.id = row->source;
        (void) hash_search(state->sources, &source, HASH_ENTER, &found);
        if (!found)
        {
            if (channel->distinct_sources == INT_MAX)
                ereport(ERROR, (errmsg("query evidence: source count overflow")));
            channel->distinct_sources++;
        }
    }

    if (!row->context_null)
    {
        QueryEvidenceKey context;
        bool found;
        MemSet(&context, 0, sizeof(context));
        context.channel_index = index->heap_index;
        context.id = row->context;
        (void) hash_search(state->contexts, &context, HASH_ENTER, &found);
        if (!found)
        {
            if (channel->distinct_contexts == INT_MAX)
                ereport(ERROR, (errmsg("query evidence: context count overflow")));
            channel->distinct_contexts++;
        }
    }

    if (state->stats)
        state->stats->observation_bindings++;
}

LaplaceQueryChannel *
laplace_query_evidence_channels(ArrayType *operands, ArrayType *types,
                                int fanout, int *count,
                                LaplaceQueryEvidenceStats *stats)
{
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext work;
    MemoryContext previous;
    Datum *values;
    bool *nulls;
    int operand_count;
    hash128_t *unique;
    int unique_count = 0;
    HASHCTL ctl;
    QueryScanState scan;
    ArrayType *unique_operands;
    LaplaceQueryChannel *result = NULL;
    int result_count = 0;

    if (!count)
        ereport(ERROR, (errmsg("query evidence: count output is required")));
    *count = 0;
    if (stats)
        MemSet(stats, 0, sizeof(*stats));
    if (!operands)
        ereport(ERROR, (errmsg("query evidence: ordered operands are required")));
    if (fanout < 0)
        ereport(ERROR, (errmsg("query evidence: fanout must not be negative")));

    validate_id_array(operands, "operands");
    validate_id_array(types, "relation types");
    if (fanout == 0 ||
        ArrayGetNItems(ARR_NDIM(operands), ARR_DIMS(operands)) == 0 ||
        (types && ArrayGetNItems(ARR_NDIM(types), ARR_DIMS(types)) == 0))
        return NULL;

    work = AllocSetContextCreate(owner, "query evidence channels", ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(work);
    deconstruct_array(operands, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &operand_count);
    if (operand_count <= 0)
    {
        MemoryContextSwitchTo(owner);
        MemoryContextDelete(work);
        return NULL;
    }
    if ((Size) operand_count > MaxAllocSize / sizeof(hash128_t) ||
        (Size) operand_count > MaxAllocSize / sizeof(int) ||
        (Size) operand_count > MaxAllocSize / sizeof(QueryBucket))
        ereport(ERROR, (errmsg("query evidence: operand set exceeds allocation capacity")));

    unique = (hash128_t *) palloc(sizeof(hash128_t) * operand_count);
    MemSet(&scan, 0, sizeof(scan));
    scan.next = (int *) palloc(sizeof(int) * operand_count);
    scan.buckets = (QueryBucket *) palloc0(sizeof(QueryBucket) * operand_count);
    scan.fanout = fanout;
    scan.owner = work;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(QueryOperandEntry);
    ctl.hcxt = work;
    scan.operands = hash_create("query evidence operands", operand_count,
                                &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(QueryChannelKey);
    ctl.entrysize = sizeof(QueryChannelIndex);
    ctl.hcxt = work;
    scan.channels = hash_create("query evidence retained channels",
                                Max(operand_count * Min(fanout, 16), 16),
                                &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = operand_count - 1; i >= 0; --i)
    {
        QueryOperandEntry *entry;
        hash128_t id;
        bool found;

        scan.next[i] = -1;
        if (nulls[i])
            continue;
        id = datum_to_hash128(values[i]);
        entry = (QueryOperandEntry *) hash_search(scan.operands, &id, HASH_ENTER, &found);
        scan.next[i] = found ? entry->first : -1;
        entry->first = i;
        if (!found)
            unique[unique_count++] = id;
    }
    if (unique_count == 0)
    {
        MemoryContextSwitchTo(owner);
        MemoryContextDelete(work);
        return NULL;
    }

    unique_operands = hash128_array_from_ids(unique, unique_count);
    scan.reverse = false;
    laplace_consensus_scan_ranked(unique_operands, NULL, types, false,
        query_consensus_cell, NULL, &scan, stats ? &stats->forward : NULL);
    scan.reverse = true;
    laplace_consensus_scan_ranked(NULL, unique_operands, types, false,
        query_consensus_cell, NULL, &scan, stats ? &stats->reverse : NULL);

    for (int i = 0; i < operand_count; ++i)
        result_count += scan.buckets[i].count;
    if (result_count == 0)
    {
        MemoryContextSwitchTo(owner);
        MemoryContextDelete(work);
        return NULL;
    }
    if ((Size) result_count > MaxAllocSize / sizeof(LaplaceQueryChannel))
        ereport(ERROR, (errmsg("query evidence: retained channel set exceeds allocation capacity")));

    MemoryContextSwitchTo(owner);
    result = (LaplaceQueryChannel *) palloc(sizeof(LaplaceQueryChannel) * result_count);
    MemoryContextSwitchTo(work);
    {
        int at = 0;
        for (int i = 0; i < operand_count; ++i)
        {
            if (scan.buckets[i].count > 0)
            {
                memcpy(result + at, scan.buckets[i].heap,
                       sizeof(LaplaceQueryChannel) * scan.buckets[i].count);
                at += scan.buckets[i].count;
            }
        }
    }

    /* Index the retained typed channels by exact query occurrence.  The raw
     * witness read is one set operation over all retained relation families and
     * observation_read remaps storage deduplication back to every ordinal. */
    {
        HTAB *relations;
        hash128_t *relation_ids;
        int relation_count = 0;
        QueryEvidenceState evidence;

        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(QueryChannelKey);
        ctl.entrysize = sizeof(QueryChannelIndex);
        ctl.hcxt = work;
        evidence.channel_index = hash_create("query evidence result index",
                                             result_count, &ctl,
                                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        for (int i = 0; i < result_count; ++i)
        {
            QueryChannelKey key = channel_key(&result[i]);
            QueryChannelIndex *entry = (QueryChannelIndex *)
                hash_search(evidence.channel_index, &key, HASH_ENTER, NULL);
            entry->heap_index = i;
        }

        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(hash128_t);
        ctl.entrysize = sizeof(hash128_t);
        ctl.hcxt = work;
        relations = hash_create("query evidence relation filter", result_count,
                                &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        for (int i = 0; i < result_count; ++i)
        {
            bool found;
            (void) hash_search(relations, &result[i].relation, HASH_ENTER, &found);
            if (!found)
                relation_count++;
        }
        relation_ids = (hash128_t *) palloc(sizeof(hash128_t) * relation_count);
        {
            HASH_SEQ_STATUS sequence;
            hash128_t *id;
            int at = 0;
            hash_seq_init(&sequence, relations);
            while ((id = (hash128_t *) hash_seq_search(&sequence)) != NULL)
                relation_ids[at++] = *id;
        }

        MemSet(&ctl, 0, sizeof(ctl));
        ctl.keysize = sizeof(QueryEvidenceKey);
        ctl.entrysize = sizeof(QueryEvidenceKey);
        ctl.hcxt = work;
        evidence.sources = hash_create("query evidence sources",
            Max(result_count * 2, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        evidence.contexts = hash_create("query evidence contexts",
            Max(result_count * 2, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        evidence.channels = result;
        evidence.stats = stats;

        laplace_observation_read(operands, NULL,
            hash128_array_from_ids(relation_ids, relation_count),
            3, query_observation, &evidence);
    }

    MemoryContextSwitchTo(owner);
    qsort(result, (size_t) result_count, sizeof(LaplaceQueryChannel), query_channel_order);
    *count = result_count;
    if (stats)
        stats->channels = (uint64) result_count;
    MemoryContextDelete(work);
    return result;
}
