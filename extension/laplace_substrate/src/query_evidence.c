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

/* One response plane per exact occurrence, relation identity and direction.
 * A populous lexical/frame/source plane cannot evict a different plane before
 * the complete observation has coupled. Dynamic relation IDs participate by
 * the same rule; no source roster or static highway bit is required. */
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

/* One exact recorded witness retained only for the duration of this set-sized
 * query coupling pass. The key begins the entry so PostgreSQL's dynahash can
 * address it directly; the payload lets later source classification reduce a
 * deterministic-calculation subset without rereading attestations. */
typedef struct QueryEvidenceWitness
{
    QueryEvidenceKey key;
    hash128_t source;
    hash128_t context;
    bool source_null;
    bool context_null;
    int16 outcome;
    int64 occurrences;
} QueryEvidenceWitness;

typedef struct QueryEvidenceState
{
    LaplaceQueryChannel *channels;
    HTAB *channel_index;
    HTAB *sources;
    HTAB *contexts;
    HTAB *provenance;
    HTAB *witnesses;
    HTAB *calculation_sources;
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

static bool
query_operand_role_valid(uint32 role)
{
    return role >= LAPLACE_QUERY_OPERAND_OBSERVATION &&
           role <= LAPLACE_QUERY_OPERAND_GEOMETRY;
}

static uint32
query_state_operand_role(const LaplaceQueryState *state, int32 ordinal)
{
    if (!state || ordinal <= 0 || ordinal > state->operand_count || !state->operand_roles)
        ereport(ERROR, (errmsg("query evidence: operand role ordinal is out of range")));
    return state->operand_roles[ordinal - 1];
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

static QueryPlaneKey
query_plane_key(int ordinal, const hash128_t *relation, bool outbound)
{
    QueryPlaneKey key;
    MemSet(&key, 0, sizeof(key));
    key.ordinal = ordinal;
    key.outbound = outbound ? 1 : 0;
    key.relation = *relation;
    return key;
}

/* Negative means a is the stronger retained channel within its typed plane.
 * This does not compare the authority of grammar, meaning or source families;
 * their separately retained responses reach the query-relative operator. */
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
    /* Standing admits a response; relation rank orders it later. An admitted
     * dynamic relation has real testimony even before a static rank exists. */
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
        query_channel_insert(state, channel.ordinal, &channel);
    }
}

/* The plane-ranked scanner invokes this only inside one exact endpoint/type
 * range. Stop after every duplicate occurrence has filled that plane, strictly
 * below its retained worst score. Equal scores still reach the deterministic
 * identity tie-breaker. A different type or direction starts its own range. */
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

static bool
add_occurrences(int64 *target, int64 value)
{
    if (value < 0 || *target > PG_INT64_MAX - value)
        return false;
    *target += value;
    return true;
}

/* Exact provenance is typed response state, not merely a source/context count.
 * Bind every witnessed row to a portable digest before reducing the set. The
 * row identity is included, but source/context/outcome/count are encoded again
 * so legacy/noncanonical row ids cannot collapse distinct provenance routes. */
static hash128_t
query_provenance_witness(const LaplaceObservation *row)
{
    unsigned char bytes[16 * 4 + 12];
    hash128_t domain;
    hash128_t zero;
    hash128_t result;
    const hash128_t *source;
    const hash128_t *context;
    Size at = 0;
    uint16 outcome = (uint16) row->outcome;
    uint64 occurrences = (uint64) row->occurrences;

    hash128_blake3_str("laplace:query-provenance-witness:v1", &domain);
    hash128_zero(&zero);
    source = row->source_null ? &zero : &row->source;
    context = row->context_null ? &zero : &row->context;

#define PROVENANCE_APPEND(value) \
    do { memcpy(bytes + at, &(value), sizeof(value)); at += sizeof(value); } while (0)
    PROVENANCE_APPEND(domain);
    PROVENANCE_APPEND(row->id);
    PROVENANCE_APPEND(*source);
    PROVENANCE_APPEND(*context);
#undef PROVENANCE_APPEND
    bytes[at++] = row->source_null ? 0 : 1;
    bytes[at++] = row->context_null ? 0 : 1;
    bytes[at++] = (unsigned char) (outcome >> 8);
    bytes[at++] = (unsigned char) outcome;
    bytes[at++] = (unsigned char) (occurrences >> 56);
    bytes[at++] = (unsigned char) (occurrences >> 48);
    bytes[at++] = (unsigned char) (occurrences >> 40);
    bytes[at++] = (unsigned char) (occurrences >> 32);
    bytes[at++] = (unsigned char) (occurrences >> 24);
    bytes[at++] = (unsigned char) (occurrences >> 16);
    bytes[at++] = (unsigned char) (occurrences >> 8);
    bytes[at++] = (unsigned char) occurrences;

    hash128_blake3(bytes, at, &result);
    return result;
}

static int
query_evidence_key_order(const void *left, const void *right)
{
    const QueryEvidenceKey *a = (const QueryEvidenceKey *) left;
    const QueryEvidenceKey *b = (const QueryEvidenceKey *) right;

    if (a->channel_index != b->channel_index)
        return a->channel_index < b->channel_index ? -1 : 1;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static void
bind_channel_provenance_roots(QueryEvidenceState *state, int channel_count)
{
    long total = hash_get_num_entries(state->provenance);
    QueryEvidenceKey *items;
    HASH_SEQ_STATUS sequence;
    QueryEvidenceKey *entry;
    long used = 0;
    hash128_t domain;

    if (total <= 0)
        return;
    if ((uint64) total > MaxAllocSize / sizeof(QueryEvidenceKey))
        ereport(ERROR,
                (errmsg("query evidence: provenance route set exceeds allocation capacity")));
    items = (QueryEvidenceKey *) palloc(sizeof(*items) * (Size) total);
    hash_seq_init(&sequence, state->provenance);
    while ((entry = (QueryEvidenceKey *) hash_seq_search(&sequence)) != NULL)
        items[used++] = *entry;
    if (used != total)
        ereport(ERROR, (errmsg("query evidence: provenance route set changed during reduction")));
    qsort(items, (size_t) total, sizeof(*items), query_evidence_key_order);
    hash128_blake3_str("laplace:query-channel-provenance:v1", &domain);

    for (long start = 0; start < total; )
    {
        long end = start + 1;
        int channel_index = items[start].channel_index;
        hash128_t *parts;
        Size member_count;

        while (end < total && items[end].channel_index == channel_index)
            ++end;
        if (channel_index < 0 || channel_index >= channel_count)
            ereport(ERROR, (errmsg("query evidence: provenance channel index is invalid")));
        member_count = (Size) (end - start);
        if (member_count >= MaxAllocSize / sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("query evidence: channel provenance set exceeds allocation capacity")));
        parts = (hash128_t *) palloc(sizeof(*parts) * (member_count + 1));
        parts[0] = domain;
        for (Size i = 0; i < member_count; ++i)
            parts[i + 1] = items[start + (long) i].id;
        hash128_merkle(0, parts, member_count + 1,
                       &state->channels[channel_index].provenance_root);
        pfree(parts);
        start = end;
    }
    pfree(items);
}

typedef struct QueryCalculationClassify
{
    HTAB *sources;
    hash128_t relation;
    hash128_t trust_class;
} QueryCalculationClassify;

static void
query_calculation_source(const LaplaceConsensusRow *row, void *opaque)
{
    QueryCalculationClassify *state = (QueryCalculationClassify *) opaque;
    bool found;

    if (row->object_is_null ||
        !hash128_eq(&row->type, &state->relation) ||
        !hash128_eq(&row->object, &state->trust_class) ||
        !(laplace_walk_edge_weight(row->rating, row->rd) > 0.0))
        return;
    (void) hash_search(state->sources, &row->subject, HASH_ENTER, &found);
}

static void
bind_calculation_source_classes(QueryEvidenceState *state, MemoryContext work)
{
    HASHCTL ctl = {0};
    HTAB *unique;
    HASH_SEQ_STATUS sequence;
    QueryEvidenceKey *source;
    hash128_t *ids;
    long count;
    long used = 0;
    ArrayType *subjects, *objects, *types;
    QueryCalculationClassify classify;

    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = work;
    unique = hash_create("query evidence unique witness sources",
                         Max((int) hash_get_num_entries(state->sources), 16),
                         &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    hash_seq_init(&sequence, state->sources);
    while ((source = (QueryEvidenceKey *) hash_seq_search(&sequence)) != NULL)
    {
        bool found;
        (void) hash_search(unique, &source->id, HASH_ENTER, &found);
    }
    count = hash_get_num_entries(unique);
    if (count <= 0)
    {
        hash_destroy(unique);
        return;
    }
    if ((uint64) count > MaxAllocSize / sizeof(hash128_t))
        ereport(ERROR,
                (errmsg("query evidence: witness source set exceeds allocation capacity")));

    ids = (hash128_t *) palloc(sizeof(*ids) * (Size) count);
    {
        hash128_t *id;
        hash_seq_init(&sequence, unique);
        while ((id = (hash128_t *) hash_seq_search(&sequence)) != NULL)
            ids[used++] = *id;
    }
    if (used != count)
        ereport(ERROR, (errmsg("query evidence: witness source set changed during classification")));
    hash_destroy(unique);

    /* HAS_TRUST_CLASS is a canonical spine relation but intentionally is not
     * governed by the generated relation-law table. Its durable identity is the
     * same canonical hash used by bootstrap and managed ingestion. */
    hash128_blake3_str("HAS_TRUST_CLASS", &classify.relation);
    hash128_blake3_str("substrate/trust_class/DerivedCalculation/v1",
                       &classify.trust_class);
    classify.sources = state->calculation_sources;

    subjects = hash128_array_from_ids(ids, (int) count);
    objects = hash128_array_from_ids(&classify.trust_class, 1);
    types = hash128_array_from_ids(&classify.relation, 1);
    laplace_consensus_scan(subjects, objects, types,
                           query_calculation_source, &classify,
                           state->stats ? &state->stats->calculation_sources : NULL);
    pfree(subjects);
    pfree(objects);
    pfree(types);
    pfree(ids);
}

static void
bind_channel_calculation_roots(QueryEvidenceState *state, int channel_count)
{
    long total = hash_get_num_entries(state->calculation_provenance);
    QueryEvidenceKey *items;
    HASH_SEQ_STATUS sequence;
    QueryEvidenceKey *entry;
    long used = 0;
    hash128_t domain;

    if (total <= 0)
        return;
    if ((uint64) total > MaxAllocSize / sizeof(QueryEvidenceKey))
        ereport(ERROR,
                (errmsg("query evidence: calculation provenance exceeds allocation capacity")));
    items = (QueryEvidenceKey *) palloc(sizeof(*items) * (Size) total);
    hash_seq_init(&sequence, state->calculation_provenance);
    while ((entry = (QueryEvidenceKey *) hash_seq_search(&sequence)) != NULL)
        items[used++] = *entry;
    if (used != total)
        ereport(ERROR,
                (errmsg("query evidence: calculation provenance changed during reduction")));
    qsort(items, (size_t) total, sizeof(*items), query_evidence_key_order);
    hash128_blake3_str("laplace:query-channel-calculation-provenance:v1", &domain);

    for (long start = 0; start < total; )
    {
        long end = start + 1;
        int channel_index = items[start].channel_index;
        hash128_t *parts;
        Size member_count;

        while (end < total && items[end].channel_index == channel_index)
            ++end;
        if (channel_index < 0 || channel_index >= channel_count)
            ereport(ERROR,
                    (errmsg("query evidence: calculation channel index is invalid")));
        member_count = (Size) (end - start);
        if (member_count >= MaxAllocSize / sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("query evidence: calculation witness set exceeds allocation capacity")));
        parts = (hash128_t *) palloc(sizeof(*parts) * (member_count + 1));
        parts[0] = domain;
        for (Size i = 0; i < member_count; ++i)
            parts[i + 1] = items[start + (long) i].id;
        hash128_merkle(0, parts, member_count + 1,
                       &state->channels[channel_index].calculation_provenance_root);
        pfree(parts);
        start = end;
    }
    pfree(items);
}

static void
bind_channel_calculations(QueryEvidenceState *state, int channel_count)
{
    HASH_SEQ_STATUS sequence;
    QueryEvidenceWitness *witness;

    bind_calculation_source_classes(state, CurrentMemoryContext);
    hash_seq_init(&sequence, state->witnesses);
    while ((witness = (QueryEvidenceWitness *) hash_seq_search(&sequence)) != NULL)
    {
        LaplaceQueryChannel *channel;
        QueryEvidenceKey key;
        bool found;

        if (witness->source_null ||
            !hash_search(state->calculation_sources, &witness->source, HASH_FIND, NULL))
            continue;
        if (witness->key.channel_index < 0 ||
            witness->key.channel_index >= channel_count)
            ereport(ERROR,
                    (errmsg("query evidence: calculation witness channel index is invalid")));

        channel = &state->channels[witness->key.channel_index];
        if (channel->calculation_rows == INT_MAX)
            ereport(ERROR, (errmsg("query evidence: calculation row count overflow")));
        channel->calculation_rows++;
        if (!add_occurrences(&channel->calculation_occurrences, witness->occurrences))
            ereport(ERROR,
                    (errmsg("query evidence: calculation occurrence count overflow")));

        switch (witness->outcome)
        {
            case LAPLACE_ATTESTATION_OUTCOME_CONFIRM:
                if (!add_occurrences(&channel->calculation_confirm_occurrences,
                                     witness->occurrences))
                    ereport(ERROR,
                            (errmsg("query evidence: calculation confirmation count overflow")));
                break;
            case LAPLACE_ATTESTATION_OUTCOME_DRAW:
                if (!add_occurrences(&channel->calculation_draw_occurrences,
                                     witness->occurrences))
                    ereport(ERROR,
                            (errmsg("query evidence: calculation draw count overflow")));
                break;
            case LAPLACE_ATTESTATION_OUTCOME_REFUTE:
                if (!add_occurrences(&channel->calculation_refute_occurrences,
                                     witness->occurrences))
                    ereport(ERROR,
                            (errmsg("query evidence: calculation refutation count overflow")));
                break;
            default:
                ereport(ERROR,
                        (errmsg("query evidence: invalid calculation outcome %d",
                                witness->outcome)));
        }

        MemSet(&key, 0, sizeof(key));
        key.channel_index = witness->key.channel_index;
        key.id = witness->source;
        (void) hash_search(state->calculation_channel_sources,
                           &key, HASH_ENTER, &found);
        if (!found)
        {
            if (channel->distinct_calculation_sources == INT_MAX)
                ereport(ERROR,
                        (errmsg("query evidence: calculation source count overflow")));
            channel->distinct_calculation_sources++;
        }

        if (!witness->context_null)
        {
            key.id = witness->context;
            (void) hash_search(state->calculation_contexts,
                               &key, HASH_ENTER, &found);
            if (!found)
            {
                if (channel->distinct_calculation_contexts == INT_MAX)
                    ereport(ERROR,
                            (errmsg("query evidence: calculation context count overflow")));
                channel->distinct_calculation_contexts++;
            }
        }

        key.id = witness->key.id;
        (void) hash_search(state->calculation_provenance,
                           &key, HASH_ENTER, NULL);
        if (state->stats)
            state->stats->calculation_bindings++;
    }
    bind_channel_calculation_roots(state, channel_count);
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

    {
        QueryEvidenceKey provenance;
        QueryEvidenceWitness *witness;
        bool found;
        MemSet(&provenance, 0, sizeof(provenance));
        provenance.channel_index = index->heap_index;
        provenance.id = query_provenance_witness(row);
        (void) hash_search(state->provenance, &provenance, HASH_ENTER, NULL);

        witness = (QueryEvidenceWitness *)
            hash_search(state->witnesses, &provenance, HASH_ENTER, &found);
        if (!found)
        {
            witness->source = row->source;
            witness->context = row->context;
            witness->source_null = row->source_null;
            witness->context_null = row->context_null;
            witness->outcome = row->outcome;
            witness->occurrences = row->occurrences;
        }
        else if (witness->source_null != row->source_null ||
                 witness->context_null != row->context_null ||
                 witness->outcome != row->outcome ||
                 witness->occurrences != row->occurrences ||
                 (!row->source_null && !hash128_eq(&witness->source, &row->source)) ||
                 (!row->context_null && !hash128_eq(&witness->context, &row->context)))
            ereport(ERROR,
                    (errmsg("query evidence: one witness digest mapped to conflicting recorded state")));
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
    MemSet(&evidence, 0, sizeof(evidence));

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
    evidence.provenance = hash_create("query evidence provenance routes",
        Max(channel_count, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    ctl.entrysize = sizeof(QueryEvidenceWitness);
    evidence.witnesses = hash_create("query evidence retained witnesses",
        Max(channel_count, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    ctl.entrysize = sizeof(QueryEvidenceKey);
    evidence.calculation_channel_sources = hash_create(
        "query calculation channel sources", Max(channel_count, 16),
        &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    evidence.calculation_contexts = hash_create(
        "query calculation contexts", Max(channel_count, 16),
        &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    evidence.calculation_provenance = hash_create(
        "query calculation provenance routes", Max(channel_count, 16),
        &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = work;
    evidence.calculation_sources = hash_create("query calculation sources",
        Max(channel_count, 16), &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    evidence.channels = channels;
    evidence.stats = stats;

    laplace_observation_read_cells(operands, NULL, cells, channel_count,
                                   query_observation, &evidence);
    bind_channel_calculations(&evidence, channel_count);
    bind_channel_provenance_roots(&evidence, channel_count);
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
    laplace_consensus_scan_ranked_planes(unique_operands, NULL, types, false,
        query_consensus_cell, query_consensus_cutoff, &scan,
        stats ? &stats->forward : NULL);
    scan.reverse = true;
    laplace_consensus_scan_ranked_planes(NULL, unique_operands, types, false,
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
        channel.operand_role = LAPLACE_QUERY_OPERAND_OBSERVATION;
        channel.anchor = *anchor;
        channel.candidate = *candidate;
        channel.relation = row->type;
        channel.outbound = !state->reverse;
        channel.rating = row->rating;
        channel.rd = row->rd;
        channel.volatility = row->volatility;
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
    if (state->operand_count > 0)
    {
        if ((Size) state->operand_count > MaxAllocSize / sizeof(uint32))
            ereport(ERROR, (errmsg("query evidence: operand role vector exceeds allocation capacity")));
        state->operand_roles = (uint32 *) palloc(sizeof(uint32) * state->operand_count);
        for (int i = 0; i < state->operand_count; ++i)
            state->operand_roles[i] = LAPLACE_QUERY_OPERAND_OBSERVATION;
    }
    initial = laplace_query_evidence_channels(operands, state->types, fanout, &count, stats);
    query_state_reserve(state, count);
    if (count > 0)
    {
        memcpy(state->channels, initial, sizeof(*initial) * count);
        state->channel_count = count;
        for (int i = 0; i < count; ++i)
            state->channels[i].operand_role =
                query_state_operand_role(state, state->channels[i].ordinal);
        pfree(initial);
    }
    MemoryContextSwitchTo(previous);
    return state;
}

void
laplace_query_state_set_operand_roles(LaplaceQueryState *state,
                                      const uint32 *roles, int role_count)
{
    MemoryContext previous;

    if (!state)
        ereport(ERROR, (errmsg("query evidence: persistent query state is required")));
    if (role_count != state->operand_count || (role_count > 0 && !roles))
        ereport(ERROR, (errmsg("query evidence: operand role vector must match operand count")));
    for (int i = 0; i < role_count; ++i)
        if (!query_operand_role_valid(roles[i]))
            ereport(ERROR, (errmsg("query evidence: invalid operand role %u", roles[i])));

    previous = MemoryContextSwitchTo(state->owner);
    if (role_count > 0)
        memcpy(state->operand_roles, roles, sizeof(uint32) * role_count);
    for (int i = 0; i < state->channel_count; ++i)
        state->channels[i].operand_role =
            query_state_operand_role(state, state->channels[i].ordinal);
    MemoryContextSwitchTo(previous);
}

static void
query_state_append_operands(LaplaceQueryState *state, ArrayType *selected,
                            int selected_count, uint32 operand_role)
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

    if (selected_count > 0)
    {
        int64 total = (int64) state->operand_count + selected_count;
        if (total > INT_MAX || (uint64) total > MaxAllocSize / sizeof(uint32))
            ereport(ERROR, (errmsg("query evidence: operand role state exceeds allocation capacity")));
        state->operand_roles = state->operand_roles
            ? (uint32 *) repalloc(state->operand_roles, sizeof(uint32) * (Size) total)
            : (uint32 *) palloc(sizeof(uint32) * (Size) total);
        for (int i = 0; i < selected_count; ++i)
            state->operand_roles[state->operand_count + i] = operand_role;
    }
}

void
laplace_query_state_extend_batch_role(LaplaceQueryState *state, ArrayType *selected,
                                      uint32 operand_role,
                                      LaplaceQueryEvidenceStats *stats)
{
    MemoryContext previous;
    LaplaceQueryChannel *added;
    int count = 0;
    int selected_count;

    if (!state)
        return;
    if (!query_operand_role_valid(operand_role))
        ereport(ERROR, (errmsg("query evidence: invalid appended operand role %u", operand_role)));
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
        added[i].operand_role = operand_role;
        state->channels[state->channel_count++] = added[i];
    }
    if (added)
        pfree(added);
    query_state_append_operands(state, selected, selected_count, operand_role);
    state->operand_count += selected_count;
    MemoryContextSwitchTo(previous);
}

void
laplace_query_state_extend_batch(LaplaceQueryState *state, ArrayType *selected,
                                 LaplaceQueryEvidenceStats *stats)
{
    laplace_query_state_extend_batch_role(
        state, selected, LAPLACE_QUERY_OPERAND_WORKING, stats);
}

void
laplace_query_state_extend(LaplaceQueryState *state, Datum selected,
                           LaplaceQueryEvidenceStats *stats)
{
    ArrayType *operand = construct_array(&selected, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    laplace_query_state_extend_batch_role(
        state, operand, LAPLACE_QUERY_OPERAND_WORKING, stats);
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
        for (int i = 0; i < exact.count; ++i)
            result[i].operand_role =
                query_state_operand_role(state, result[i].ordinal);
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
