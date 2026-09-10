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
    int channel_count;
    int channel_capacity;
    int operand_count;
    int fanout;
};

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

/* Negative means a is the stronger retained channel. Candidate generation is
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

/* laplace_consensus_scan_ranked walks each endpoint range in descending
 * (rating - 2*rd) order. Stop one endpoint/partition only after every duplicate
 * occurrence of that exact operand has filled its own bounded typed heap and
 * the current score is STRICTLY below every retained worst score. Equal-score
 * rows must still run so deterministic candidate/relation tie election remains
 * exact across partitions. */
static bool
query_consensus_cutoff(const LaplaceConsensusRow *row, void *opaque)
{
    QueryScanState *state = (QueryScanState *) opaque;
    const hash128_t *anchor;
    QueryOperandEntry *operand;
    __int128 score;

    if (row->object_is_null)
        return false;
    anchor = state->reverse ? &row->object : &row->subject;
    operand = (QueryOperandEntry *) hash_search(state->operands, anchor, HASH_FIND, NULL);
    if (!operand)
        return false;
    score = (__int128) row->rating - 2 * (__int128) row->rd;

    for (int index = operand->first; index >= 0; index = state->next[index])
    {
        QueryBucket *bucket = &state->buckets[index];
        __int128 worst;

        if (bucket->count < state->fanout)
            return false;
        worst = (__int128) bucket->heap[0].rating -
                2 * (__int128) bucket->heap[0].rd;
        if (score >= worst)
            return false;
    }
    return true;
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

static void
bind_channel_observations(ArrayType *operands, LaplaceQueryChannel *channels,
                          int channel_count, LaplaceQueryEvidenceStats *stats,
                          MemoryContext work)
{
    HASHCTL ctl;
    LaplaceObservationCell *cells;
    QueryEvidenceState evidence;

    if (channel_count <= 0)
        return;
    cells = palloc(sizeof(*cells) * channel_count);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(QueryChannelKey);
    ctl.entrysize = sizeof(QueryChannelIndex);
    ctl.hcxt = work;
    evidence.channel_index = hash_create("query evidence result index",
                                         channel_count, &ctl,
                                         HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (int i = 0; i < channel_count; ++i)
    {
        QueryChannelKey key = channel_key(&channels[i]);
        QueryChannelIndex *entry = (QueryChannelIndex *)
            hash_search(evidence.channel_index, &key, HASH_ENTER, NULL);
        entry->heap_index = i;
        cells[i].subject = channels[i].outbound ? channels[i].anchor : channels[i].candidate;
        cells[i].type = channels[i].relation;
        cells[i].object = channels[i].outbound ? channels[i].candidate : channels[i].anchor;
    }

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(QueryEvidenceKey);
    ctl.entrysize = sizeof(QueryEvidenceKey);
    ctl.hcxt = work;
    evidence.sources = hash_create("query evidence sources",
        Max(channel_count, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    evidence.contexts = hash_create("query evidence contexts",
        Max(channel_count, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    evidence.channels = channels;
    evidence.stats = stats;

    laplace_observation_read_cells(operands, NULL, cells, channel_count,
                                   query_observation, &evidence);
    pfree(cells);
}

LaplaceQueryChannel *
laplace_query_evidence_channels(ArrayType *operands, ArrayType *types,
                                int fanout, int *count,
                                LaplaceQueryEvidenceStats *stats)
{
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext work;
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
    MemoryContextSwitchTo(work);
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
                                Max(operand_count, 16),
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
        query_consensus_cell, query_consensus_cutoff, &scan,
        stats ? &stats->forward : NULL);
    scan.reverse = true;
    laplace_consensus_scan_ranked(NULL, unique_operands, types, false,
        query_consensus_cell, query_consensus_cutoff, &scan,
        stats ? &stats->reverse : NULL);

    {
        int64 retained = 0;
        for (int i = 0; i < operand_count; ++i)
        {
            retained += scan.buckets[i].count;
            if (retained > INT_MAX)
                ereport(ERROR, (errmsg("query evidence: retained channel count exceeds int capacity")));
        }
        result_count = (int) retained;
    }
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

    bind_channel_observations(operands, result, result_count, stats, work);

    MemoryContextSwitchTo(owner);
    qsort(result, (size_t) result_count, sizeof(LaplaceQueryChannel), query_channel_order);
    *count = result_count;
    if (stats)
        stats->channels = (uint64) result_count;
    MemoryContextDelete(work);
    return result;
}

static void
query_exact_reserve(QueryExactState *state, int additional)
{
    int64 needed = (int64) state->count + additional;
    int capacity;

    if (additional < 0 || needed > INT_MAX ||
        (uint64) needed > MaxAllocSize / sizeof(LaplaceQueryChannel))
        ereport(ERROR, (errmsg("query evidence: exact candidate channel set exceeds allocation capacity")));
    if (needed <= state->capacity)
        return;
    capacity = state->capacity ? state->capacity : 32;
    while (capacity < needed)
    {
        int64 grown = (int64) capacity * 2;
        capacity = grown > INT_MAX ? (int) needed : (int) Min(grown, (int64) INT_MAX);
    }
    state->channels = state->channels
        ? (LaplaceQueryChannel *) repalloc(state->channels,
                                           sizeof(LaplaceQueryChannel) * capacity)
        : (LaplaceQueryChannel *) palloc(sizeof(LaplaceQueryChannel) * capacity);
    state->capacity = capacity;
}

static void
query_exact_cell(const LaplaceConsensusRow *row, void *opaque)
{
    QueryExactState *state = (QueryExactState *) opaque;
    const hash128_t *anchor;
    const hash128_t *candidate;
    QueryOperandEntry *operand;

    if (row->object_is_null)
        return;
    anchor = state->reverse ? &row->object : &row->subject;
    candidate = state->reverse ? &row->subject : &row->object;
    operand = (QueryOperandEntry *) hash_search(state->operands, anchor, HASH_FIND, NULL);
    if (!operand)
        return;

    for (int index = operand->first; index >= 0; index = state->next[index])
    {
        LaplaceQueryChannel channel;
        QueryChannelKey key;
        QueryChannelIndex *entry;
        bool found;

        MemSet(&channel, 0, sizeof(channel));
        channel.ordinal = index + 1;
        channel.anchor = *anchor;
        channel.candidate = *candidate;
        channel.relation = row->type;
        channel.outbound = !state->reverse;
        channel.rating = row->rating;
        channel.rd = row->rd;
        channel.witnesses = row->witnesses;
        key = channel_key(&channel);
        entry = (QueryChannelIndex *)
            hash_search(state->channel_index, &key, HASH_ENTER, &found);
        if (found)
        {
            LaplaceQueryChannel *prior = &state->channels[entry->heap_index];
            if (query_channel_rank(&channel, prior) < 0)
                *prior = channel;
            continue;
        }

        query_exact_reserve(state, 1);
        entry->heap_index = state->count;
        state->channels[state->count++] = channel;
    }
}

static void
query_state_reserve(LaplaceQueryState *state, int additional)
{
    int64 needed = (int64) state->channel_count + additional;
    int capacity;

    if (additional < 0 || needed > INT_MAX ||
        (uint64) needed > MaxAllocSize / sizeof(LaplaceQueryChannel))
        ereport(ERROR, (errmsg("query evidence: persistent channel state exceeds allocation capacity")));
    if (needed <= state->channel_capacity)
        return;
    capacity = state->channel_capacity ? state->channel_capacity : 16;
    while (capacity < needed)
    {
        int64 grown = (int64) capacity * 2;
        capacity = grown > INT_MAX ? (int) needed : (int) Min(grown, (int64) INT_MAX);
        if (capacity == INT_MAX && capacity < needed)
            capacity = (int) needed;
    }
    state->channels = state->channels
        ? (LaplaceQueryChannel *) repalloc(state->channels,
                                           sizeof(LaplaceQueryChannel) * capacity)
        : (LaplaceQueryChannel *) palloc(sizeof(LaplaceQueryChannel) * capacity);
    state->channel_capacity = capacity;
}

LaplaceQueryState *
laplace_query_state_create(ArrayType *operands, ArrayType *types, int fanout,
                           LaplaceQueryEvidenceStats *stats)
{
    MemoryContext parent = CurrentMemoryContext;
    MemoryContext owner;
    MemoryContext previous;
    LaplaceQueryState *state;
    LaplaceQueryChannel *initial;
    int count = 0;

    validate_id_array(operands, "operands");
    validate_id_array(types, "relation types");
    if (!operands)
        ereport(ERROR, (errmsg("query evidence: ordered operands are required")));
    if (fanout < 0)
        ereport(ERROR, (errmsg("query evidence: fanout must not be negative")));

    owner = AllocSetContextCreate(parent, "query evidence state", ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(owner);
    state = (LaplaceQueryState *) palloc0(sizeof(*state));
    state->owner = owner;
    state->fanout = fanout;
    state->operand_count = ArrayGetNItems(ARR_NDIM(operands), ARR_DIMS(operands));
    state->operands = DatumGetArrayTypePCopy(PointerGetDatum(operands));
    state->types = types ? DatumGetArrayTypePCopy(PointerGetDatum(types)) : NULL;
    initial = laplace_query_evidence_channels(operands, state->types, fanout, &count, stats);
    query_state_reserve(state, count);
    if (count > 0)
    {
        memcpy(state->channels, initial, sizeof(*initial) * count);
        state->channel_count = count;
        pfree(initial);
    }
    MemoryContextSwitchTo(previous);
    return state;
}

static void
query_state_append_operands(LaplaceQueryState *state, ArrayType *selected)
{
    ArrayBuildState *build = NULL;
    ArrayIterator iterator;
    Datum value;
    bool isnull;
    ArrayType *previous = state->operands;

    iterator = array_create_iterator(previous, 0, NULL);
    while (array_iterate(iterator, &value, &isnull))
        build = accumArrayResult(build, value, isnull, BYTEAOID, state->owner);
    array_free_iterator(iterator);
    iterator = array_create_iterator(selected, 0, NULL);
    while (array_iterate(iterator, &value, &isnull))
        build = accumArrayResult(build, value, isnull, BYTEAOID, state->owner);
    array_free_iterator(iterator);
    state->operands = DatumGetArrayTypeP(makeArrayResult(build, state->owner));
    pfree(previous);
}

void
laplace_query_state_extend_batch(LaplaceQueryState *state, ArrayType *selected,
                                 LaplaceQueryEvidenceStats *stats)
{
    MemoryContext previous;
    LaplaceQueryChannel *added;
    int count = 0;
    int selected_count;

    if (!state)
        return;
    if (stats) MemSet(stats, 0, sizeof(*stats));
    validate_id_array(selected, "selected frontier");
    if (!selected) return;
    selected_count = ArrayGetNItems(ARR_NDIM(selected), ARR_DIMS(selected));
    if (selected_count == 0) return;
    if (selected_count > INT_MAX - state->operand_count)
        ereport(ERROR, (errmsg("query evidence: working-state ordinal exceeds int capacity")));

    previous = MemoryContextSwitchTo(state->owner);
    added = laplace_query_evidence_channels(selected, state->types,
                                            state->fanout, &count, stats);
    query_state_reserve(state, count);
    for (int i = 0; i < count; ++i)
    {
        added[i].ordinal += state->operand_count;
        state->channels[state->channel_count++] = added[i];
    }
    if (added)
        pfree(added);
    state->operand_count += selected_count;
    query_state_append_operands(state, selected);
    MemoryContextSwitchTo(previous);
}

void
laplace_query_state_extend(LaplaceQueryState *state, Datum selected,
                           LaplaceQueryEvidenceStats *stats)
{
    ArrayType *operand = construct_array(&selected, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    laplace_query_state_extend_batch(state, operand, stats);
    pfree(operand);
}

const LaplaceQueryChannel *
laplace_query_state_channels(const LaplaceQueryState *state, int *count)
{
    if (!count)
        ereport(ERROR, (errmsg("query evidence: channel count output is required")));
    *count = state ? state->channel_count : 0;
    return state ? state->channels : NULL;
}

LaplaceQueryChannel *
laplace_query_state_candidate_evidence(const LaplaceQueryState *state,
                                       ArrayType *candidates, int *count,
                                       LaplaceQueryEvidenceStats *stats)
{
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext work;
    Datum *operand_values, *candidate_values;
    bool *operand_nulls, *candidate_nulls;
    int operand_count, candidate_count;
    hash128_t *unique_operands, *unique_candidates;
    int unique_operand_count = 0, unique_candidate_count = 0;
    HASHCTL ctl;
    HTAB *operand_seen, *candidate_seen;
    QueryExactState exact;
    ArrayType *operand_ids, *candidate_ids;
    LaplaceQueryChannel *result = NULL;

    if (!count)
        ereport(ERROR, (errmsg("query evidence: candidate evidence count output is required")));
    *count = 0;
    if (stats)
        MemSet(stats, 0, sizeof(*stats));
    if (!state)
        ereport(ERROR, (errmsg("query evidence: persistent query state is required")));
    if (!candidates)
        ereport(ERROR, (errmsg("query evidence: candidate identities are required")));
    validate_id_array(candidates, "candidates");
    if (ArrayGetNItems(ARR_NDIM(candidates), ARR_DIMS(candidates)) == 0 ||
        state->operand_count == 0 ||
        (state->types && ArrayGetNItems(ARR_NDIM(state->types), ARR_DIMS(state->types)) == 0))
        return NULL;

    work = AllocSetContextCreate(owner, "query candidate evidence", ALLOCSET_DEFAULT_SIZES);
    MemoryContextSwitchTo(work);
    deconstruct_array(state->operands, BYTEAOID, -1, false, TYPALIGN_INT,
                      &operand_values, &operand_nulls, &operand_count);
    deconstruct_array(candidates, BYTEAOID, -1, false, TYPALIGN_INT,
                      &candidate_values, &candidate_nulls, &candidate_count);
    if ((Size) operand_count > MaxAllocSize / sizeof(hash128_t) ||
        (Size) operand_count > MaxAllocSize / sizeof(int) ||
        (Size) candidate_count > MaxAllocSize / sizeof(hash128_t))
        ereport(ERROR, (errmsg("query evidence: exact candidate operand set exceeds allocation capacity")));

    unique_operands = (hash128_t *) palloc(sizeof(hash128_t) * Max(operand_count, 1));
    unique_candidates = (hash128_t *) palloc(sizeof(hash128_t) * Max(candidate_count, 1));
    exact.next = (int *) palloc(sizeof(int) * Max(operand_count, 1));
    exact.channels = NULL;
    exact.count = exact.capacity = 0;
    exact.reverse = false;
    exact.owner = work;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(QueryOperandEntry);
    ctl.hcxt = work;
    exact.operands = hash_create("query exact operands", Max(operand_count, 16),
                                 &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ctl.entrysize = sizeof(hash128_t);
    operand_seen = hash_create("query exact operand ids", Max(operand_count, 16),
                               &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    candidate_seen = hash_create("query exact candidate ids", Max(candidate_count, 16),
                                 &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(QueryChannelKey);
    ctl.entrysize = sizeof(QueryChannelIndex);
    ctl.hcxt = work;
    exact.channel_index = hash_create("query exact channel index", 128, &ctl,
                                      HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = operand_count - 1; i >= 0; --i)
    {
        QueryOperandEntry *entry;
        hash128_t id;
        bool found;
        exact.next[i] = -1;
        if (operand_nulls[i])
            continue;
        id = datum_to_hash128(operand_values[i]);
        entry = (QueryOperandEntry *) hash_search(exact.operands, &id, HASH_ENTER, &found);
        exact.next[i] = found ? entry->first : -1;
        entry->first = i;
        (void) hash_search(operand_seen, &id, HASH_ENTER, &found);
        if (!found)
            unique_operands[unique_operand_count++] = id;
    }
    for (int i = 0; i < candidate_count; ++i)
    {
        hash128_t id;
        bool found;
        if (candidate_nulls[i])
            ereport(ERROR, (errmsg("query evidence: candidate identities must not contain NULL")));
        id = datum_to_hash128(candidate_values[i]);
        (void) hash_search(candidate_seen, &id, HASH_ENTER, &found);
        if (!found)
            unique_candidates[unique_candidate_count++] = id;
    }
    if (unique_operand_count == 0 || unique_candidate_count == 0)
    {
        MemoryContextSwitchTo(owner);
        MemoryContextDelete(work);
        return NULL;
    }

    operand_ids = hash128_array_from_ids(unique_operands, unique_operand_count);
    candidate_ids = hash128_array_from_ids(unique_candidates, unique_candidate_count);
    exact.reverse = false;
    laplace_consensus_scan(operand_ids, candidate_ids, state->types,
                           query_exact_cell, &exact,
                           stats ? &stats->forward : NULL);
    exact.reverse = true;
    laplace_consensus_scan(candidate_ids, operand_ids, state->types,
                           query_exact_cell, &exact,
                           stats ? &stats->reverse : NULL);

    if (exact.count > 0)
    {
        MemoryContextSwitchTo(owner);
        result = (LaplaceQueryChannel *) palloc(sizeof(*result) * exact.count);
        memcpy(result, exact.channels, sizeof(*result) * exact.count);
        MemoryContextSwitchTo(work);
        bind_channel_observations(state->operands, result, exact.count, stats, work);
        MemoryContextSwitchTo(owner);
        qsort(result, (size_t) exact.count, sizeof(*result), query_channel_order);
        *count = exact.count;
        if (stats)
            stats->channels = (uint64) exact.count;
    }
    else
        MemoryContextSwitchTo(owner);

    MemoryContextDelete(work);
    return result;
}

static ArrayType *
query_state_distinct_ids(const LaplaceQueryState *state, bool relations)
{
    MemoryContext owner = CurrentMemoryContext;
    HASHCTL ctl;
    HTAB *seen;
    ArrayBuildState *values = NULL;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = owner;
    seen = hash_create(relations ? "query state relation ids" : "query state candidate ids",
                       state && state->channel_count > 0 ? state->channel_count : 16,
                       &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    if (state)
    {
        for (int i = 0; i < state->channel_count; ++i)
        {
            const hash128_t *id = relations
                ? &state->channels[i].relation : &state->channels[i].candidate;
            bool found;
            (void) hash_search(seen, id, HASH_ENTER, &found);
            if (!found)
                values = accumArrayResult(values, hash128_to_datum(id), false,
                                          BYTEAOID, owner);
        }
    }
    hash_destroy(seen);
    return values
        ? DatumGetArrayTypeP(makeArrayResult(values, owner))
        : construct_empty_array(BYTEAOID);
}

ArrayType *
laplace_query_state_candidates(const LaplaceQueryState *state)
{
    return query_state_distinct_ids(state, false);
}

ArrayType *
laplace_query_state_relation_types(const LaplaceQueryState *state)
{
    return query_state_distinct_ids(state, true);
}

void
laplace_query_state_destroy(LaplaceQueryState **state)
{
    MemoryContext owner;

    if (!state || !*state)
        return;
    owner = (*state)->owner;
    *state = NULL;
    MemoryContextDelete(owner);
}
