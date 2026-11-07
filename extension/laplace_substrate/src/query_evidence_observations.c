/*
 * query_evidence_observations.c — Attestation witness binding, calculation
 * reduction, and Merkle provenance root computation for query channels.
 */
#include "query_evidence_internal.h"

static bool
add_occurrences(int64 *target, int64 value)
{
    if (value < 0 || *target > PG_INT64_MAX - value)
        return false;
    *target += value;
    return true;
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

    hash_seq_init(&sequence, state->witnesses);
    while ((witness = (QueryEvidenceWitness *) hash_seq_search(&sequence)) != NULL)
    {
        LaplaceQueryChannel *channel;
        QueryEvidenceKey key;
        bool found;

        if (!witness->calculation)
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
            case LAPLACE_OBSERVATION_SCORED:
                break;
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
        case LAPLACE_OBSERVATION_SCORED:
            break;
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
        provenance.id = laplace_observation_provenance(row);
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
            witness->calculation = laplace_observation_qualified(row, "derivation", "calculation");
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
        if (row->native && channel->native_claim)
        {
            (void) hash_search(state->native_sources, &source, HASH_ENTER, &found);
            if (!found)
            {
                if (channel->witnesses == PG_INT64_MAX) elog(ERROR, "Native witness count overflow");
                channel->witnesses++;
            }
        }
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

void
laplace_qe_bind_channel_observations(ArrayType *operands, LaplaceQueryChannel *channels,
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
    evidence.native_sources = hash_create("query Native witness sources",
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

    evidence.channels = channels;
    evidence.stats = stats;

    laplace_observation_read_cells(operands, NULL, cells, channel_count,
                                   query_observation, &evidence);
    bind_channel_calculations(&evidence, channel_count);
    bind_channel_provenance_roots(&evidence, channel_count);
    pfree(cells);
}
