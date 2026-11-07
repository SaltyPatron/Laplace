/*
 * query_evidence.c — Bounded per-plane channel heaps, consensus scans, and
 * exact candidate evidence queries.
 */
#include "query_evidence_internal.h"

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
    QueryPlaneKey plane = query_plane_key(ordinal, &channel->relation, channel->outbound);
    bool found;
    QueryBucket *bucket = (QueryBucket *)
        hash_search(state->buckets, &plane, HASH_ENTER, &found);
    QueryChannelKey key = channel_key(channel);
    QueryChannelIndex *entry = (QueryChannelIndex *)
        hash_search(state->channels, &key, HASH_FIND, NULL);
    if (!found)
    {
        bucket->heap = NULL;
        bucket->count = bucket->capacity = 0;
    }

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
                    (errmsg("query evidence: per-plane fanout exceeds allocation capacity")));
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
    if (!(laplace_walk_edge_weight(row->rating, row->rd) > 0.0))
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
        channel.operand_role = LAPLACE_QUERY_OPERAND_OBSERVATION;
        channel.anchor = *anchor;
        channel.candidate = *candidate;
        channel.relation = row->type;
        channel.outbound = !state->reverse;
        channel.rating = row->rating;
        channel.rd = row->rd;
        channel.volatility = row->volatility;
        channel.witnesses = row->witnesses;
        channel.claim = row->claim;
        channel.native_claim = row->native_claim;
        query_channel_insert(state, channel.ordinal, &channel);
    }
}

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
        QueryPlaneKey plane = query_plane_key(index + 1, &row->type, !state->reverse);
        QueryBucket *bucket = (QueryBucket *)
            hash_search(state->buckets, &plane, HASH_FIND, NULL);
        __int128 worst;

        if (bucket == NULL || bucket->count < state->fanout)
            return false;
        worst = (__int128) bucket->heap[0].rating -
                2 * (__int128) bucket->heap[0].rd;
        if (score >= worst)
            return false;
    }
    return true;
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

    laplace_qe_validate_id_array(operands, "operands");
    laplace_qe_validate_id_array(types, "relation types");
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
        (Size) operand_count > MaxAllocSize / sizeof(int))
        ereport(ERROR, (errmsg("query evidence: operand set exceeds allocation capacity")));

    unique = (hash128_t *) palloc(sizeof(hash128_t) * operand_count);
    MemSet(&scan, 0, sizeof(scan));
    scan.next = (int *) palloc(sizeof(int) * operand_count);
    scan.fanout = fanout;
    scan.owner = work;

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(QueryOperandEntry);
    ctl.hcxt = work;
    scan.operands = hash_create("query evidence operands", operand_count,
                                &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(QueryPlaneKey);
    ctl.entrysize = sizeof(QueryBucket);
    ctl.hcxt = work;
    scan.buckets = hash_create("query evidence typed planes", Max(operand_count, 16),
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
    laplace_consensus_ranked_candidates(unique_operands, NULL, types, false, true,
        query_consensus_cell, query_consensus_cutoff, &scan,
        stats ? &stats->forward : NULL);

    scan.reverse = true;
    laplace_consensus_ranked_candidates(NULL, unique_operands, types, false, true,
        query_consensus_cell, query_consensus_cutoff, &scan,
        stats ? &stats->reverse : NULL);

    {
        int64 retained = 0;
        HASH_SEQ_STATUS sequence;
        QueryBucket *bucket;
        hash_seq_init(&sequence, scan.buckets);
        while ((bucket = hash_seq_search(&sequence)) != NULL)
        {
            retained += bucket->count;
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
        HASH_SEQ_STATUS sequence;
        QueryBucket *bucket;
        hash_seq_init(&sequence, scan.buckets);
        while ((bucket = hash_seq_search(&sequence)) != NULL)
        {
            if (bucket->count > 0)
            {
                memcpy(result + at, bucket->heap,
                       sizeof(LaplaceQueryChannel) * bucket->count);
                at += bucket->count;
            }
        }
    }

    laplace_qe_bind_channel_observations(operands, result, result_count, stats, work);

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
        channel.operand_role = LAPLACE_QUERY_OPERAND_OBSERVATION;
        channel.anchor = *anchor;
        channel.candidate = *candidate;
        channel.relation = row->type;
        channel.outbound = !state->reverse;
        channel.rating = row->rating;
        channel.rd = row->rd;
        channel.volatility = row->volatility;
        channel.witnesses = row->witnesses;
        channel.claim = row->claim;
        channel.native_claim = row->native_claim;
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
    laplace_qe_validate_id_array(candidates, "candidates");
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
    laplace_consensus_candidates(operand_ids, candidate_ids, state->types,
                                 query_exact_cell, &exact,
                                 stats ? &stats->forward : NULL);

    exact.reverse = true;
    laplace_consensus_candidates(candidate_ids, operand_ids, state->types,
                                 query_exact_cell, &exact,
                                 stats ? &stats->reverse : NULL);

    if (exact.count > 0)
    {
        MemoryContextSwitchTo(owner);
        result = (LaplaceQueryChannel *) palloc(sizeof(*result) * exact.count);
        memcpy(result, exact.channels, sizeof(*result) * exact.count);
        MemoryContextSwitchTo(work);
        laplace_qe_bind_channel_observations(state->operands, result, exact.count, stats, work);
        MemoryContextSwitchTo(owner);
        for (int i = 0; i < exact.count; ++i)
            result[i].operand_role =
                laplace_qe_state_operand_role(state, result[i].ordinal);
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
