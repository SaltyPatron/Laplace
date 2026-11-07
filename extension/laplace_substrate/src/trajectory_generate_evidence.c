/*
 * trajectory_generate_evidence.c — Query operand construction, occurrence
 * provenance tracking, channel comparisons, and typed evidence summaries.
 */
#include "trajectory_generate_internal.h"

void
laplace_tg_validate_id_array(ArrayType *array, const char *name, bool allow_nulls)
{
    Datum *values;
    bool *nulls;
    int count;

    if (!array)
        return;
    if (ARR_NDIM(array) > 1 || ARR_ELEMTYPE(array) != BYTEAOID)
        ereport(ERROR,
                (errmsg("forward execution: %s must be a one-dimensional bytea array", name)));
    deconstruct_array(array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i])
        {
            if (!allow_nulls)
                ereport(ERROR,
                        (errmsg("forward execution: %s must not contain NULL", name)));
            continue;
        }
        if (VARSIZE_ANY_EXHDR(DatumGetByteaPP(values[i])) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward execution: %s identities must be 16 bytes", name)));
    }
    if (count > 0)
    {
        pfree(values);
        pfree(nulls);
    }
}

bool
laplace_tg_relation_can_traverse_reverse(const hash128_t *type)
{
    const laplace_relation_def_t *def = NULL;
    return laplace_relation_lookup(type, &def) == 0 && def != NULL &&
           def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC;
}

HTAB *
laplace_tg_new_id_index(const char *name, long expected, MemoryContext owner, Size entry_size)
{
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entry_size;
    ctl.hcxt = owner;
    return hash_create(name, Max(expected, 16), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

void
laplace_tg_prompt_frontier_add(PromptFrontier *frontier, const hash128_t *id)
{
    bool found;
    hash_search(frontier->seen, id, HASH_ENTER, &found);
    if (!found)
        frontier->ids = accumArrayResult(frontier->ids, hash128_to_datum(id),
                                         false, BYTEAOID, frontier->owner);
}

ArrayType *
laplace_tg_query_operands(ArrayType *ordered, ArrayType *frontier, ArrayType *discourse,
                          MemoryContext owner, uint32 **roles_out, int *role_count)
{
    Datum *ordered_values = NULL, *frontier_values = NULL, *discourse_values = NULL;
    bool *ordered_nulls = NULL, *frontier_nulls = NULL, *discourse_nulls = NULL;
    int ordered_count = 0, frontier_count = 0, discourse_count = 0;
    Datum *combined;
    uint32 *roles;
    int used = 0;
    HTAB *seen = laplace_tg_new_id_index("forward query operand identities", 128, owner,
                                         sizeof(hash128_t));

    if (!roles_out || !role_count)
        ereport(ERROR, (errmsg("forward execution: operand role outputs are required")));
    *roles_out = NULL;
    *role_count = 0;

    deconstruct_array(ordered, BYTEAOID, -1, false, TYPALIGN_INT,
                      &ordered_values, &ordered_nulls, &ordered_count);
    if (frontier)
        deconstruct_array(frontier, BYTEAOID, -1, false, TYPALIGN_INT,
                          &frontier_values, &frontier_nulls, &frontier_count);
    if (discourse)
        deconstruct_array(discourse, BYTEAOID, -1, false, TYPALIGN_INT,
                          &discourse_values, &discourse_nulls, &discourse_count);

    uint64 capacity = (uint64) ordered_count + (uint64) frontier_count +
                      (uint64) discourse_count;
    if (capacity > INT_MAX || capacity > MaxAllocSize / sizeof(Datum) ||
        capacity > MaxAllocSize / sizeof(uint32))
        ereport(ERROR,
                (errmsg("forward execution: query operand set exceeds allocation capacity")));
    combined = palloc(sizeof(Datum) * Max((int) capacity, 1));
    roles = palloc(sizeof(uint32) * Max((int) capacity, 1));

    for (int i = 0; i < ordered_count; ++i)
    {
        bytea *value;
        bool found;
        if (ordered_nulls[i])
            continue;
        value = DatumGetByteaPP(ordered_values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward execution: ordered query identities must be 16 bytes")));
        combined[used] = ordered_values[i];
        roles[used++] = LAPLACE_QUERY_OPERAND_OBSERVATION;
        hash_search(seen, VARDATA_ANY(value), HASH_ENTER, &found);
    }

    for (int i = 0; i < frontier_count; ++i)
    {
        bytea *value;
        bool found;
        if (frontier_nulls[i])
            continue;
        value = DatumGetByteaPP(frontier_values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward execution: supplemental query identities must be 16 bytes")));
        hash_search(seen, VARDATA_ANY(value), HASH_ENTER, &found);
        if (!found)
        {
            combined[used] = frontier_values[i];
            roles[used++] = LAPLACE_QUERY_OPERAND_SEMANTIC_SEED;
        }
    }

    for (int i = 0; i < discourse_count; ++i)
    {
        bytea *value;
        if (discourse_nulls[i])
            continue;
        value = DatumGetByteaPP(discourse_values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("forward execution: discourse identities must be 16 bytes")));
        combined[used] = discourse_values[i];
        roles[used++] = LAPLACE_QUERY_OPERAND_DISCOURSE;
    }

    ArrayType *result = construct_array(combined, used, BYTEAOID, -1, false, TYPALIGN_INT);
    hash_destroy(seen);
    pfree(combined);
    if (ordered_count > 0)
    {
        pfree(ordered_values);
        pfree(ordered_nulls);
    }
    if (frontier_count > 0)
    {
        pfree(frontier_values);
        pfree(frontier_nulls);
    }
    if (discourse_count > 0)
    {
        pfree(discourse_values);
        pfree(discourse_nulls);
    }
    *roles_out = roles;
    *role_count = used;
    return result;
}

OriginEntry *
laplace_tg_origin_get(HTAB *origins, const hash128_t *id, bool create)
{
    bool found;
    OriginEntry *entry = hash_search(origins, id, create ? HASH_ENTER : HASH_FIND,
                                     create ? &found : NULL);
    if (create && entry && !found)
        entry->occurrences = NULL;
    return entry;
}

void
laplace_tg_origin_merge(HTAB *origins, const hash128_t *target, const hash128_t *source,
                        MemoryContext owner)
{
    OriginEntry *from = laplace_tg_origin_get(origins, source, false);
    if (!from || !from->occurrences)
        return;
    MemoryContext previous = MemoryContextSwitchTo(owner);
    OriginEntry *to = laplace_tg_origin_get(origins, target, true);
    to->occurrences = bms_add_members(to->occurrences, from->occurrences);
    MemoryContextSwitchTo(previous);
}

void
laplace_tg_origin_add_occurrences(HTAB *origins, const hash128_t *target,
                                  const Bitmapset *occurrences, MemoryContext owner)
{
    MemoryContext previous;
    OriginEntry *to;

    if (!occurrences)
        return;
    previous = MemoryContextSwitchTo(owner);
    to = laplace_tg_origin_get(origins, target, true);
    to->occurrences = bms_add_members(to->occurrences, occurrences);
    MemoryContextSwitchTo(previous);
}

void
laplace_tg_origin_inherit_sequence(HTAB *origins, const hash128_t *target,
                                   Datum *context, int context_length, int stride,
                                   MemoryContext owner)
{
    int begin;
    if (stride <= 0 || context_length <= 0)
        return;
    begin = Max(0, context_length - stride);
    for (int i = begin; i < context_length; ++i)
    {
        hash128_t source = datum_to_hash128(context[i]);
        laplace_tg_origin_merge(origins, target, &source, owner);
    }
}

void
laplace_tg_propagate_candidate_origins(HTAB *origins, HTAB *candidate_index,
                                       const LaplaceQueryChannel *channels, int count,
                                       const LaplacePromptIntent *intent,
                                       MemoryContext owner)
{
    (void) intent;
    for (int i = 0; i < count; ++i)
    {
        const LaplaceQueryChannel *channel = &channels[i];
        if (!hash_search(candidate_index, &channel->candidate, HASH_FIND, NULL))
            continue;
        if (channel->operand_role == LAPLACE_QUERY_OPERAND_GEOMETRY ||
            (!channel->outbound && !laplace_tg_relation_can_traverse_reverse(&channel->relation)) ||
            !(laplace_walk_edge_weight(channel->rating, channel->rd) > 0.0) ||
            !walk_relation_salient(channel->relation) ||
            laplace_prompt_contract_relation(&channel->relation))
            continue;
        laplace_tg_origin_merge(origins, &channel->candidate, &channel->anchor, owner);
    }
}

int
laplace_tg_positive_channel_compare(const LaplaceQueryChannel *a,
                                    const LaplaceQueryChannel *b)
{
    double arank = walk_relation_rank(a->relation);
    double brank = walk_relation_rank(b->relation);
    __int128 aconservative = (__int128) a->rating - 2 * (__int128) a->rd;
    __int128 bconservative = (__int128) b->rating - 2 * (__int128) b->rd;
    int cmp;

    if (arank != brank)
        return arank > brank ? 1 : -1;
    if (aconservative != bconservative)
        return aconservative > bconservative ? 1 : -1;
    if (a->rd != b->rd)
        return a->rd < b->rd ? 1 : -1;
    if (a->witnesses != b->witnesses)
        return a->witnesses > b->witnesses ? 1 : -1;
    if (a->distinct_sources != b->distinct_sources)
        return a->distinct_sources > b->distinct_sources ? 1 : -1;
    if (a->distinct_contexts != b->distinct_contexts)
        return a->distinct_contexts > b->distinct_contexts ? 1 : -1;
    if (a->confirm_occurrences != b->confirm_occurrences)
        return a->confirm_occurrences > b->confirm_occurrences ? 1 : -1;
    if (a->refute_occurrences != b->refute_occurrences)
        return a->refute_occurrences < b->refute_occurrences ? 1 : -1;
    if (a->outbound != b->outbound)
        return a->outbound ? 1 : -1;
    cmp = memcmp(&a->relation, &b->relation, sizeof(hash128_t));
    if (cmp != 0)
        return cmp < 0 ? 1 : -1;
    cmp = memcmp(&a->anchor, &b->anchor, sizeof(hash128_t));
    if (cmp != 0)
        return cmp < 0 ? 1 : -1;
    return 0;
}

int
laplace_tg_negative_channel_compare(const LaplaceQueryChannel *a,
                                    const LaplaceQueryChannel *b)
{
    double arank = walk_relation_rank(a->relation);
    double brank = walk_relation_rank(b->relation);
    __int128 aupper = (__int128) a->rating + 2 * (__int128) a->rd;
    __int128 bupper = (__int128) b->rating + 2 * (__int128) b->rd;
    int cmp;

    if (arank != brank)
        return arank > brank ? 1 : -1;
    if (aupper != bupper)
        return aupper < bupper ? 1 : -1;
    if (a->rd != b->rd)
        return a->rd < b->rd ? 1 : -1;
    if (a->witnesses != b->witnesses)
        return a->witnesses > b->witnesses ? 1 : -1;
    if (a->distinct_sources != b->distinct_sources)
        return a->distinct_sources > b->distinct_sources ? 1 : -1;
    if (a->distinct_contexts != b->distinct_contexts)
        return a->distinct_contexts > b->distinct_contexts ? 1 : -1;
    if (a->refute_occurrences != b->refute_occurrences)
        return a->refute_occurrences > b->refute_occurrences ? 1 : -1;
    if (a->confirm_occurrences != b->confirm_occurrences)
        return a->confirm_occurrences < b->confirm_occurrences ? 1 : -1;
    if (a->outbound != b->outbound)
        return a->outbound ? 1 : -1;
    cmp = memcmp(&a->relation, &b->relation, sizeof(hash128_t));
    if (cmp != 0)
        return cmp < 0 ? 1 : -1;
    cmp = memcmp(&a->anchor, &b->anchor, sizeof(hash128_t));
    if (cmp != 0)
        return cmp < 0 ? 1 : -1;
    return 0;
}

static void
cover_origins(HTAB *coverage, HTAB *origins, const LaplaceQueryChannel *channel,
              uint8 polarity, int32 *covered)
{
    OriginEntry *origin = hash_search(origins, &channel->anchor, HASH_FIND, NULL);
    EvidenceCoverageKey key = {0};
    int member = -1;
    if (!origin || !origin->occurrences)
        return;
    key.id = channel->candidate;
    key.polarity = polarity;
    while ((member = bms_next_member(origin->occurrences, member)) >= 0)
    {
        bool found;
        key.ordinal = member;
        hash_search(coverage, &key, HASH_ENTER, &found);
        if (!found)
        {
            if (*covered == PG_INT32_MAX)
                elog(ERROR, "forward execution: occurrence coverage overflow");
            ++*covered;
        }
    }
}

HTAB *
laplace_tg_evidence_summaries_from_channels(const LaplaceQueryChannel *channels, int count,
                                            bool projection_only, HTAB *origins,
                                            const Bitmapset *required,
                                            const LaplaceCognitionProgram *cognition,
                                            MemoryContext owner)
{
    HTAB *summaries = laplace_tg_new_id_index(
        projection_only ? "forward projection evidence" : "forward query evidence",
        count, owner, sizeof(EvidenceEntry));
    HASHCTL coverage_ctl = {0};
    HASHCTL relation_ctl = {0};
    coverage_ctl.keysize = sizeof(EvidenceCoverageKey);
    coverage_ctl.entrysize = sizeof(EvidenceCoverage);
    coverage_ctl.hcxt = owner;
    relation_ctl.keysize = sizeof(EvidenceRelationKey);
    relation_ctl.entrysize = sizeof(EvidenceRelation);
    relation_ctl.hcxt = owner;
    HTAB *coverage = hash_create(projection_only ? "forward projection occurrence coverage" :
                                 "forward query occurrence coverage", Max(count, 16),
                                 &coverage_ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    HTAB *relations = hash_create(projection_only ? "forward projection relation coverage" :
                                  "forward query relation coverage", Max(count, 16),
                                  &relation_ctl,
                                  HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (int i = 0; i < count; ++i)
    {
        const LaplaceQueryChannel *channel = &channels[i];
        EvidenceEntry *entry;
        EvidenceRelationKey relation_key;
        double sign;
        bool found, relation_found;

        if (channel->operand_role == LAPLACE_QUERY_OPERAND_GEOMETRY)
            continue;

        if (projection_only && !channel->outbound &&
            !laplace_tg_relation_can_traverse_reverse(&channel->relation))
            continue;

        entry = hash_search(summaries, &channel->candidate, HASH_ENTER, &found);
        if (!found)
            MemSet(&entry->summary, 0, sizeof(entry->summary));

        sign = laplace_walk_edge_weight(channel->rating, channel->rd);
        if (!(sign > 0.0 && isfinite(sign)) && !(sign < 0.0 && isfinite(sign)))
        {
            if (channel->calculation_confirm_occurrences > 0)
                sign = 1.0;
            else if (channel->calculation_refute_occurrences > 0)
                sign = -1.0;
        }
        if (sign > 0.0 && isfinite(sign))
        {
            if (walk_relation_salient(channel->relation))
            {
                cover_origins(coverage, origins, channel, 1,
                              &entry->summary.positive_covered_occurrences);
                const Bitmapset *grounded = required
                    ? laplace_cognition_program_semantic_origins(cognition, &channel->anchor)
                    : NULL;
                int member = -1;
                while ((member = bms_next_member(grounded, member)) >= 0)
                {
                    EvidenceCoverageKey key = {0};
                    bool found;
                    if (!bms_is_member(member, required))
                        continue;
                    key.id = channel->candidate;
                    key.ordinal = member;
                    key.polarity = 3;
                    hash_search(coverage, &key, HASH_ENTER, &found);
                    if (!found)
                        entry->summary.positive_required_occurrences++;
                }
            }

            MemSet(&relation_key, 0, sizeof(relation_key));
            relation_key.id = channel->candidate;
            relation_key.relation = channel->relation;
            relation_key.polarity = 1;
            hash_search(relations, &relation_key, HASH_ENTER, &relation_found);
            if (!relation_found)
            {
                if (entry->summary.positive_relation_families == PG_INT32_MAX)
                    ereport(ERROR,
                            (errmsg("forward execution: positive relation family coverage overflow")));
                entry->summary.positive_relation_families++;
            }

            if (!entry->summary.has_positive ||
                laplace_tg_positive_channel_compare(channel, &entry->summary.positive) > 0)
            {
                entry->summary.positive = *channel;
                entry->summary.has_positive = true;
            }
        }
        else if (sign < 0.0 && isfinite(sign))
        {
            if (walk_relation_salient(channel->relation))
                cover_origins(coverage, origins, channel, 2,
                              &entry->summary.negative_covered_occurrences);

            MemSet(&relation_key, 0, sizeof(relation_key));
            relation_key.id = channel->candidate;
            relation_key.relation = channel->relation;
            relation_key.polarity = 2;
            hash_search(relations, &relation_key, HASH_ENTER, &relation_found);
            if (!relation_found)
            {
                if (entry->summary.negative_relation_families == PG_INT32_MAX)
                    ereport(ERROR,
                            (errmsg("forward execution: negative relation family coverage overflow")));
                entry->summary.negative_relation_families++;
            }

            if (!entry->summary.has_negative ||
                laplace_tg_negative_channel_compare(channel, &entry->summary.negative) > 0)
            {
                entry->summary.negative = *channel;
                entry->summary.has_negative = true;
            }
        }
    }

    hash_destroy(relations);
    hash_destroy(coverage);
    return summaries;
}

HTAB *
laplace_tg_evidence_summaries(const LaplaceQueryState *state, bool projection_only,
                              HTAB *origins, const Bitmapset *required,
                              const LaplaceCognitionProgram *cognition, MemoryContext owner)
{
    int count = 0;
    const LaplaceQueryChannel *channels = laplace_query_state_channels(state, &count);
    return laplace_tg_evidence_summaries_from_channels(
        channels, count, projection_only, origins, required, cognition, owner);
}

int
laplace_tg_positive_summary_compare(const EvidenceSummary *a, const EvidenceSummary *b)
{
    int cmp;
    if (a->has_positive != b->has_positive)
        return a->has_positive ? 1 : -1;
    if (!a->has_positive)
        return 0;
    if (a->positive_required_occurrences != b->positive_required_occurrences)
        return a->positive_required_occurrences > b->positive_required_occurrences ? 1 : -1;
    if (a->positive_covered_occurrences != b->positive_covered_occurrences)
        return a->positive_covered_occurrences > b->positive_covered_occurrences ? 1 : -1;
    if (a->positive_relation_families != b->positive_relation_families)
        return a->positive_relation_families > b->positive_relation_families ? 1 : -1;
    cmp = laplace_tg_positive_channel_compare(&a->positive, &b->positive);
    return cmp;
}

int
laplace_tg_opposition_summary_compare(const EvidenceSummary *a, const EvidenceSummary *b)
{
    int cmp;
    if (a->has_negative != b->has_negative)
        return a->has_negative ? -1 : 1;
    if (!a->has_negative)
        return 0;
    cmp = laplace_tg_negative_channel_compare(&a->negative, &b->negative);
    if (cmp != 0)
        return -cmp;
    if (a->negative_covered_occurrences != b->negative_covered_occurrences)
        return a->negative_covered_occurrences < b->negative_covered_occurrences ? 1 : -1;
    if (a->negative_relation_families != b->negative_relation_families)
        return a->negative_relation_families < b->negative_relation_families ? 1 : -1;
    return 0;
}
