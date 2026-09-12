/*
 * trajectory_generate.c — canonical query-relative forward execution.
 *
 * One retained query state owns the semantic frontier for the whole pass.
 * Ordered physical continuation evidence and typed relation/evidence channels
 * are independent candidate providers; selection extends both the ordered
 * trajectory and the persistent query state before the next election.
 */
#include "postgres.h"

#include <math.h>

#include "catalog/pg_type.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "nodes/bitmapset.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"

#include "laplace/core/hash128.h"

#include "cognition_program.h"
#include "prompt_input.h"
#include "query_evidence.h"
#include "relation_symmetry.h"
#include "spi_common.h"
#include "trajectory_continuations.h"
#include "walk_score.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_continuations);
PG_FUNCTION_INFO_V1(pg_laplace_forward_prompt);
PG_FUNCTION_INFO_V1(pg_laplace_forward_trace);

typedef struct EvidenceSummary
{
    bool has_positive;
    bool has_negative;
    LaplaceQueryChannel positive;
    LaplaceQueryChannel negative;
    int32 positive_covered_occurrences;
    int32 positive_relation_families;
    int32 negative_covered_occurrences;
    int32 negative_relation_families;
} EvidenceSummary;

typedef struct Candidate
{
    hash128_t id;
    int64 sequence_occurrences;
    int stride;
    EvidenceSummary query;
    EvidenceSummary query_traversal;
    EvidenceSummary projection;
} Candidate;

typedef struct CandidateIndex
{
    hash128_t id;
    int index;
} CandidateIndex;

typedef struct EvidenceEntry
{
    hash128_t id;
    EvidenceSummary summary;
} EvidenceEntry;

typedef struct OriginEntry
{
    hash128_t id;
    Bitmapset *occurrences;
} OriginEntry;

typedef struct EvidenceCoverageKey
{
    hash128_t id;
    int32 ordinal;
    uint8 polarity;
    uint8 reserved[3];
} EvidenceCoverageKey;

typedef struct EvidenceCoverage
{
    EvidenceCoverageKey key;
} EvidenceCoverage;

typedef struct EvidenceRelationKey
{
    hash128_t id;
    hash128_t relation;
    uint8 polarity;
    uint8 reserved[7];
} EvidenceRelationKey;

typedef struct EvidenceRelation
{
    EvidenceRelationKey key;
} EvidenceRelation;

typedef struct PromptFrontier
{
    HTAB *seen;
    ArrayBuildState *ids;
    MemoryContext owner;
} PromptFrontier;

static uint64
splitmix64(uint64 *state)
{
    uint64 z = (*state += UINT64CONST(0x9E3779B97F4A7C15));
    z = (z ^ (z >> 30)) * UINT64CONST(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)) * UINT64CONST(0x94D049BB133111EB);
    return z ^ (z >> 31);
}

static double
rng_uniform(uint64 *state)
{
    return ((double) (splitmix64(state) >> 11) + 0.5) *
           (1.0 / 9007199254740992.0);
}

static void
validate_id_array(ArrayType *array, const char *name, bool allow_nulls)
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

static bool
relation_can_traverse_reverse(const hash128_t *type)
{
    const laplace_relation_def_t *def = NULL;
    return laplace_relation_lookup(type, &def) == 0 && def != NULL &&
           def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC;
}

static HTAB *
new_id_index(const char *name, long expected, MemoryContext owner, Size entry_size)
{
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = entry_size;
    ctl.hcxt = owner;
    return hash_create(name, Max(expected, 16), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static void
prompt_frontier_add(PromptFrontier *frontier, const hash128_t *id)
{
    bool found;
    hash_search(frontier->seen, id, HASH_ENTER, &found);
    if (!found)
        frontier->ids = accumArrayResult(frontier->ids, hash128_to_datum(id),
                                         false, BYTEAOID, frontier->owner);
}

static ArrayType *
query_operands(ArrayType *ordered, ArrayType *frontier, MemoryContext owner)
{
    Datum *ordered_values = NULL, *frontier_values = NULL;
    bool *ordered_nulls = NULL, *frontier_nulls = NULL;
    int ordered_count = 0, frontier_count = 0;
    Datum *combined;
    int used = 0;
    HTAB *seen = new_id_index("forward query operand identities", 128, owner,
                              sizeof(hash128_t));

    deconstruct_array(ordered, BYTEAOID, -1, false, TYPALIGN_INT,
                      &ordered_values, &ordered_nulls, &ordered_count);
    if (frontier)
        deconstruct_array(frontier, BYTEAOID, -1, false, TYPALIGN_INT,
                          &frontier_values, &frontier_nulls, &frontier_count);

    if ((uint64) ordered_count + (uint64) frontier_count > INT_MAX ||
        (uint64) (ordered_count + frontier_count) > MaxAllocSize / sizeof(Datum))
        ereport(ERROR,
                (errmsg("forward execution: query operand set exceeds allocation capacity")));
    combined = palloc(sizeof(Datum) * Max(ordered_count + frontier_count, 1));

    /* Exact ordered occurrences remain exact occurrences. `seen` is used only
     * to prevent the supplemental frontier from duplicating identities that
     * are already represented by the ordered request. */
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
        combined[used++] = ordered_values[i];
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
            combined[used++] = frontier_values[i];
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
    return result;
}

static OriginEntry *
origin_get(HTAB *origins, const hash128_t *id, bool create)
{
    bool found;
    OriginEntry *entry = hash_search(origins, id, create ? HASH_ENTER : HASH_FIND,
                                     create ? &found : NULL);
    if (create && entry && !found)
        entry->occurrences = NULL;
    return entry;
}

static void
origin_merge(HTAB *origins, const hash128_t *target, const hash128_t *source,
             MemoryContext owner)
{
    OriginEntry *from = origin_get(origins, source, false);
    if (!from || !from->occurrences)
        return;
    MemoryContext previous = MemoryContextSwitchTo(owner);
    OriginEntry *to = origin_get(origins, target, true);
    to->occurrences = bms_add_members(to->occurrences, from->occurrences);
    MemoryContextSwitchTo(previous);
}

/* Structural continuity has exact ancestry too. A successor supported by a
 * matched suffix inherits the occurrence roots of that exact suffix; it is not
 * a provenance-free token merely because its provider is physicality rather
 * than testimony. */
static void
origin_inherit_sequence(HTAB *origins, const hash128_t *target,
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
        origin_merge(origins, target, &source, owner);
    }
}

static void
propagate_candidate_origins(HTAB *origins, HTAB *candidate_index,
                            const LaplaceQueryChannel *channels, int count,
                            MemoryContext owner)
{
    for (int i = 0; i < count; ++i)
    {
        const LaplaceQueryChannel *channel = &channels[i];
        if (!hash_search(candidate_index, &channel->candidate, HASH_FIND, NULL))
            continue;
        if ((!channel->outbound && !relation_can_traverse_reverse(&channel->relation)) ||
            !(walk_edge_score(channel->relation, channel->rating, channel->rd) > 0.0))
            continue;
        origin_merge(origins, &channel->candidate, &channel->anchor, owner);
    }
}

static int
positive_channel_compare(const LaplaceQueryChannel *a,
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

static int
negative_channel_compare(const LaplaceQueryChannel *a,
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

/* Build a typed evidence index without reducing different relation families,
 * occurrences, source diversity, and standing into one scalar. The channel set
 * may be a bounded proposal field or the exact bounded-candidate adjudication
 * field; the election tuple itself is identical. */
static void
cover_origins(HTAB *coverage, HTAB *origins, const LaplaceQueryChannel *channel,
              uint8 polarity, int32 *covered)
{
    OriginEntry *origin = hash_search(origins, &channel->anchor, HASH_FIND, NULL);
    EvidenceCoverageKey key = {0};
    int member = -1;
    if (!origin)
        elog(ERROR, "forward execution: evidence anchor lost its query provenance");
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

static HTAB *
evidence_summaries_from_channels(const LaplaceQueryChannel *channels, int count,
                                 bool projection_only, HTAB *origins, MemoryContext owner)
{
    HTAB *summaries = new_id_index(projection_only ? "forward projection evidence" :
                                   "forward query evidence", count, owner,
                                   sizeof(EvidenceEntry));
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

        /* Incoming asymmetric testimony is valid retained evidence, but an
         * output projection may not silently invert its traversal law. */
        if (projection_only && !channel->outbound &&
            !relation_can_traverse_reverse(&channel->relation))
            continue;

        entry = hash_search(summaries, &channel->candidate, HASH_ENTER, &found);
        if (!found)
            MemSet(&entry->summary, 0, sizeof(entry->summary));

        sign = walk_edge_score(channel->relation, channel->rating, channel->rd);
        if (sign > 0.0 && isfinite(sign))
        {
            cover_origins(coverage, origins, channel, 1,
                          &entry->summary.positive_covered_occurrences);

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
                positive_channel_compare(channel, &entry->summary.positive) > 0)
            {
                entry->summary.positive = *channel;
                entry->summary.has_positive = true;
            }
        }
        else if (sign < 0.0 && isfinite(sign))
        {
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
                negative_channel_compare(channel, &entry->summary.negative) > 0)
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

static HTAB *
evidence_summaries(const LaplaceQueryState *state, bool projection_only,
                   HTAB *origins, MemoryContext owner)
{
    int count = 0;
    const LaplaceQueryChannel *channels = laplace_query_state_channels(state, &count);
    return evidence_summaries_from_channels(channels, count, projection_only, origins, owner);
}

static int
positive_summary_compare(const EvidenceSummary *a, const EvidenceSummary *b)
{
    int cmp;
    if (a->has_positive != b->has_positive)
        return a->has_positive ? 1 : -1;
    if (!a->has_positive)
        return 0;
    /* Elect against the joint query before comparing one supporting cell.
     * Previously the cell's endpoint hash could settle the comparison before
     * coverage was even inspected, so one strong isolated edge defeated a
     * candidate supported by the complete declared operand set. */
    if (a->positive_covered_occurrences != b->positive_covered_occurrences)
        return a->positive_covered_occurrences > b->positive_covered_occurrences ? 1 : -1;
    if (a->positive_relation_families != b->positive_relation_families)
        return a->positive_relation_families > b->positive_relation_families ? 1 : -1;
    cmp = positive_channel_compare(&a->positive, &b->positive);
    return cmp;
}

/* For opposition, absence is preferable. When both candidates are opposed,
 * the one with the weaker strongest refutation wins. */
static int
opposition_summary_compare(const EvidenceSummary *a, const EvidenceSummary *b)
{
    int cmp;
    if (a->has_negative != b->has_negative)
        return a->has_negative ? -1 : 1;
    if (!a->has_negative)
        return 0;
    cmp = negative_channel_compare(&a->negative, &b->negative);
    if (cmp != 0)
        return -cmp;
    if (a->negative_covered_occurrences != b->negative_covered_occurrences)
        return a->negative_covered_occurrences < b->negative_covered_occurrences ? 1 : -1;
    if (a->negative_relation_families != b->negative_relation_families)
        return a->negative_relation_families < b->negative_relation_families ? 1 : -1;
    return 0;
}

static bool
context_contains_id(Datum *context, int count, const hash128_t *id)
{
    for (int i = 0; i < count; ++i)
    {
        if (memcmp(VARDATA_ANY(DatumGetByteaPP(context[i])),
                   id, sizeof(hash128_t)) == 0)
            return true;
    }
    return false;
}

static int
candidate_add(Candidate **items, int *count, int *capacity, HTAB *index,
              const hash128_t *id, int64 occurrences, int stride)
{
    CandidateIndex *entry;
    bool found;

    entry = hash_search(index, id, HASH_ENTER, &found);
    if (found)
    {
        Candidate *candidate = &(*items)[entry->index];
        if (stride > candidate->stride ||
            (stride == candidate->stride && occurrences > candidate->sequence_occurrences))
        {
            candidate->stride = stride;
            candidate->sequence_occurrences = occurrences;
        }
        return entry->index;
    }

    if (*count == *capacity)
    {
        int64 grown = *capacity ? (int64) *capacity * 2 : 32;
        if (grown > INT_MAX || (uint64) grown > MaxAllocSize / sizeof(Candidate))
            ereport(ERROR,
                    (errmsg("forward execution: candidate set exceeds allocation capacity")));
        *items = *items ? repalloc(*items, sizeof(Candidate) * (Size) grown)
                        : palloc(sizeof(Candidate) * (Size) grown);
        *capacity = (int) grown;
    }
    entry->index = *count;
    (*items)[*count] = (Candidate) {
        .id = *id,
        .sequence_occurrences = occurrences,
        .stride = stride,
        .query = {0},
        .query_traversal = {0},
        .projection = {0},
    };
    return (*count)++;
}

static ArrayType *
candidate_id_array(const Candidate *candidates, int count)
{
    Datum *ids;
    ArrayType *array;

    if (count <= 0)
        return construct_empty_array(BYTEAOID);
    if ((Size) count > MaxAllocSize / sizeof(Datum))
        ereport(ERROR,
                (errmsg("forward execution: candidate identity array exceeds allocation capacity")));
    ids = palloc(sizeof(Datum) * count);
    for (int i = 0; i < count; ++i)
        ids[i] = hash128_to_datum(&candidates[i].id);
    array = construct_array(ids, count, BYTEAOID, -1, false, TYPALIGN_INT);
    for (int i = 0; i < count; ++i)
        pfree(DatumGetPointer(ids[i]));
    pfree(ids);
    return array;
}

static int
candidate_compare(const void *left, const void *right)
{
    const Candidate *a = left;
    const Candidate *b = right;
    int cmp;

    /* Declared election tuple: output-purpose testimony, query testimony,
     * exact structural match, observed recurrence, then opposition. No cross-
     * family score product or universal adjacency scalar is materialized. */
    cmp = positive_summary_compare(&a->projection, &b->projection);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = positive_summary_compare(&a->query_traversal, &b->query_traversal);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = positive_summary_compare(&a->query, &b->query);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    if (a->stride != b->stride)
        return a->stride > b->stride ? -1 : 1;
    if (a->sequence_occurrences != b->sequence_occurrences)
        return a->sequence_occurrences > b->sequence_occurrences ? -1 : 1;
    cmp = opposition_summary_compare(&a->query, &b->query);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = opposition_summary_compare(&a->projection, &b->projection);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static void
put_cognition_receipt(Datum *values, bool *nulls, int base,
                      const LaplaceCognitionProgram *program)
{
    LaplaceCognitionProgramReceipt receipt;
    laplace_cognition_program_receipt(program, &receipt);
    values[base + 0] = hash128_to_datum(&receipt.program_id);
    values[base + 1] = Int32GetDatum(receipt.required_obligations);
    values[base + 2] = Int32GetDatum(receipt.satisfied_obligations);
    values[base + 3] = Int32GetDatum(receipt.remaining_required);
    values[base + 4] = BoolGetDatum(receipt.complete);
    values[base + 5] = CStringGetTextDatum(
        laplace_cognition_disposition_name(receipt.disposition));
    if (receipt.output_present)
        values[base + 6] = hash128_to_datum(&receipt.output_fingerprint);
    else
        nulls[base + 6] = true;
    if (receipt.semantic_act_present)
        values[base + 7] = hash128_to_datum(&receipt.semantic_act_id);
    else
        nulls[base + 7] = true;
    values[base + 8] = Int32GetDatum(receipt.output_count);
}

static void
free_cognition_receipt(Datum *values, bool *nulls, int base)
{
    pfree(DatumGetPointer(values[base + 0]));
    pfree(DatumGetPointer(values[base + 5]));
    if (!nulls[base + 6]) pfree(DatumGetPointer(values[base + 6]));
    if (!nulls[base + 7]) pfree(DatumGetPointer(values[base + 7]));
}

static void
emit_result(ReturnSetInfo *result, int32 step, const Candidate *candidate,
            bool trace, const LaplacePromptInput *input, int candidate_count,
            int operand_count, int query_channels, int exact_channels,
            bool routing, int routing_round,
            const LaplaceCognitionProgram *program)
{
    Datum values[33] = {0};
    bool nulls[33] = {false, false, false, true};
    Datum id = hash128_to_datum(&candidate->id);
    values[0] = Int32GetDatum(step);
    values[1] = id;
    values[2] = Int32GetDatum(candidate->stride);
    values[3] = (Datum) 0;
    if (trace)
    {
        const EvidenceSummary *support = candidate->projection.has_positive
            ? &candidate->projection : candidate->query_traversal.has_positive
            ? &candidate->query_traversal : &candidate->query;
        const LaplaceQueryChannel *channel = &support->positive;
        values[4] = hash128_to_datum(&input->root);
        values[5] = Int32GetDatum(candidate_count);
        values[6] = Int32GetDatum(operand_count);
        values[7] = Int32GetDatum(query_channels);
        values[8] = Int32GetDatum(exact_channels);
        values[9] = Int64GetDatum(candidate->sequence_occurrences);
        values[10] = Int32GetDatum(support->positive_covered_occurrences);
        values[11] = Int32GetDatum(support->positive_relation_families);
        values[12] = Int32GetDatum(candidate->query.negative_covered_occurrences);
        if (support->has_positive)
        {
            values[13] = hash128_to_datum(&channel->anchor);
            values[14] = hash128_to_datum(&channel->relation);
            values[15] = BoolGetDatum(channel->outbound);
            values[16] = Int64GetDatum(channel->rating);
            values[17] = Int64GetDatum(channel->rd);
            values[18] = Int64GetDatum(channel->witnesses);
            values[19] = Int32GetDatum(channel->distinct_sources);
            values[20] = Int32GetDatum(channel->distinct_contexts);
        }
        else
            for (int i = 13; i <= 20; ++i) nulls[i] = true;
        values[21] = BoolGetDatum(candidate->projection.has_positive);
        values[22] = CStringGetTextDatum(routing ? "route" : "emit");
        values[23] = Int32GetDatum(routing_round);
        put_cognition_receipt(values, nulls, 24, program);
    }
    tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
    pfree(DatumGetPointer(id));
    if (trace)
    {
        pfree(DatumGetPointer(values[4]));
        if (!nulls[13]) pfree(DatumGetPointer(values[13]));
        if (!nulls[14]) pfree(DatumGetPointer(values[14]));
        pfree(DatumGetPointer(values[22]));
        free_cognition_receipt(values, nulls, 24);
    }
}

static void
emit_terminal(ReturnSetInfo *result, int32 step, const LaplacePromptInput *input,
              int operand_count, int routing_round,
              const LaplaceCognitionProgram *program)
{
    Datum values[33] = {0};
    bool nulls[33] = {false};
    LaplaceCognitionProgramReceipt receipt;

    for (int i = 0; i < 33; ++i) nulls[i] = true;
    laplace_cognition_program_receipt(program, &receipt);
    values[0] = Int32GetDatum(step); nulls[0] = false;
    values[4] = hash128_to_datum(&input->root); nulls[4] = false;
    values[5] = Int32GetDatum(0); nulls[5] = false;
    values[6] = Int32GetDatum(operand_count); nulls[6] = false;
    values[7] = Int32GetDatum(0); nulls[7] = false;
    values[8] = Int32GetDatum(0); nulls[8] = false;
    values[9] = Int64GetDatum(0); nulls[9] = false;
    values[10] = Int32GetDatum(0); nulls[10] = false;
    values[11] = Int32GetDatum(0); nulls[11] = false;
    values[12] = Int32GetDatum(0); nulls[12] = false;
    values[19] = Int32GetDatum(0); nulls[19] = false;
    values[20] = Int32GetDatum(0); nulls[20] = false;
    values[21] = BoolGetDatum(false); nulls[21] = false;
    values[22] = CStringGetTextDatum(receipt.complete ? "complete" : "unresolved");
    nulls[22] = false;
    values[23] = Int32GetDatum(routing_round); nulls[23] = false;
    for (int i = 24; i < 33; ++i) nulls[i] = false;
    put_cognition_receipt(values, nulls, 24, program);
    tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
    pfree(DatumGetPointer(values[4]));
    pfree(DatumGetPointer(values[22]));
    free_cognition_receipt(values, nulls, 24);
}

static Datum
walk_continuations(FunctionCallInfo fcinfo, const LaplacePromptInput *input,
                   int semantic_hop_limit, bool trace)
{
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    ArrayType *context_array;
    ArrayType *frontier_array = NULL;
    ArrayType *relation_types = NULL;
    ArrayType *output_relations = NULL;
    int32 steps, max_stride, top_k, fanout;
    float8 spread;
    uint64 rng;
    Datum *context_values;
    bool *context_nulls;
    int context_count;
    Datum *context;
    int context_length = 0;
    int context_capacity;
    MemoryContext walk_context, step_context, old;
    LaplaceTrajectoryScope *trajectory_scope = NULL;
    LaplaceQueryState *query_state = NULL;
    LaplaceQueryState *output_state = NULL;
    LaplaceCognitionProgram *cognition = NULL;
    HTAB *route_seen;
    HTAB *origins;
    int next_origin = 0;
    int semantic_hops = 0;
    bool exhausted = false;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("forward execution: context must not be NULL")));
    context_array = PG_GETARG_ARRAYTYPE_P(0);
    steps = PG_ARGISNULL(1) ? 24 : PG_GETARG_INT32(1);
    max_stride = PG_ARGISNULL(2) ? 5 : PG_GETARG_INT32(2);
    spread = PG_ARGISNULL(3) ? 0.7 : PG_GETARG_FLOAT8(3);
    top_k = PG_ARGISNULL(4) ? 10 : PG_GETARG_INT32(4);
    rng = PG_ARGISNULL(5) ? UINT64CONST(0x5851F42D4C957F2D) :
                            (uint64) PG_GETARG_INT64(5);
    if (PG_NARGS() > 6 && !PG_ARGISNULL(6))
        frontier_array = PG_GETARG_ARRAYTYPE_P(6);
    if (PG_NARGS() > 7 && !PG_ARGISNULL(7))
        relation_types = PG_GETARG_ARRAYTYPE_P(7);
    fanout = PG_NARGS() > 8 && !PG_ARGISNULL(8) ? PG_GETARG_INT32(8) : 8;
    if (PG_NARGS() > 11 && !PG_ARGISNULL(11))
        output_relations = PG_GETARG_ARRAYTYPE_P(11);

    if (steps < 0 || max_stride < 0 || top_k < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("forward execution: steps, stride, top-k and fanout must not be negative")));
    if (!isfinite(spread) || spread < 0.0)
        ereport(ERROR,
                (errmsg("forward execution: spread must be finite and not negative")));
    validate_id_array(context_array, "context", true);
    validate_id_array(frontier_array, "frontier", true);
    validate_id_array(relation_types, "relation types", false);
    validate_id_array(output_relations, "output relation types", false);

    InitMaterializedSRF(fcinfo, 0);
    if (steps == 0 || top_k == 0)
        return (Datum) 0;

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "forward execution: SPI_connect failed");

    walk_context = CurrentMemoryContext;
    deconstruct_array(context_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &context_values, &context_nulls, &context_count);
    if ((uint64) context_count + (uint64) steps > INT_MAX ||
        (uint64) (context_count + steps) > MaxAllocSize / sizeof(Datum))
        ereport(ERROR,
                (errmsg("forward execution: retained ordered context exceeds allocation capacity")));
    context_capacity = context_count + steps;
    context = palloc(sizeof(Datum) * Max(context_capacity, 1));
    for (int i = 0; i < context_count; ++i)
    {
        if (context_nulls[i])
            continue;
        context[context_length++] = PointerGetDatum(PG_DETOAST_DATUM_COPY(context_values[i]));
    }
    if (context_count > 0)
    {
        pfree(context_values);
        pfree(context_nulls);
    }
    if (context_length == 0)
    {
        SPI_finish();
        return (Datum) 0;
    }

    old = MemoryContextSwitchTo(walk_context);
    ArrayType *operands = query_operands(context_array, frontier_array, walk_context);
    query_state = laplace_query_state_create(operands, relation_types, fanout, NULL);
    origins = new_id_index("forward occurrence provenance", 128, walk_context, sizeof(OriginEntry));
    route_seen = new_id_index("forward admitted routing identities", 128,
                              walk_context, sizeof(hash128_t));
    ArrayIterator route_iterator = array_create_iterator(operands, 0, NULL);
    Datum route_value;
    bool route_null;
    while (array_iterate(route_iterator, &route_value, &route_null))
    {
        if (route_null) continue;
        hash128_t id = datum_to_hash128(route_value);
        hash_search(route_seen, &id, HASH_ENTER, NULL);
        OriginEntry *origin = origin_get(origins, &id, true);
        origin->occurrences = bms_add_member(origin->occurrences, next_origin++);
    }
    array_free_iterator(route_iterator);

    if (input)
    {
        /* The Merkle root denotes this complete exact observation. Its
         * provenance is therefore the union of the admitted prompt occurrence
         * coordinates, not a synthetic unrelated seed. This lets a declared
         * root-result relation close obligations from the whole prompt. */
        OriginEntry *root_origin = origin_get(origins, &input->root, true);
        for (int i = 0; i < context_length; ++i)
            root_origin->occurrences = bms_add_member(root_origin->occurrences, i);
        int initial_channel_count = 0;
        const LaplaceQueryChannel *initial_channels =
            laplace_query_state_channels(query_state, &initial_channel_count);
        cognition = laplace_cognition_program_create(input, context_length,
                                                      initial_channels,
                                                      initial_channel_count);
    }

    if (output_relations &&
        ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) > 0)
    {
        ArrayType *output_operands;
        if (input)
        {
            Datum root = hash128_to_datum(&input->root);
            output_operands = construct_array(&root, 1, BYTEAOID, -1, false, TYPALIGN_INT);
            pfree(DatumGetPointer(root));
        }
        else
        {
            /* Explicit output projection is evaluated against the same active
             * query operand frontier as steering. Ordered context remains the
             * independent sequence operand; supplemental semantic frontier ids
             * gain no synthetic trajectory stride by participating here. */
            output_operands = DatumGetArrayTypePCopy(PointerGetDatum(operands));
        }
        output_state = laplace_query_state_create(output_operands, output_relations,
                                                  fanout, NULL);
        pfree(output_operands);
    }
    pfree(operands);

    if (PG_NARGS() > 9 && !PG_ARGISNULL(9) && max_stride > 0)
    {
        trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend(trajectory_scope, PG_GETARG_ARRAYTYPE_P(9));
    }
    if (PG_NARGS() > 10 && !PG_ARGISNULL(10) && max_stride > 0)
    {
        if (!trajectory_scope)
            trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend_containing(trajectory_scope,
                                                   PG_GETARG_ARRAYTYPE_P(10));
    }
    if (input && max_stride > 0)
    {
        if (!trajectory_scope)
            trajectory_scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_bind_input(trajectory_scope, input);
    }
    MemoryContextSwitchTo(old);

    step_context = AllocSetContextCreate(walk_context, "forward query election",
                                         ALLOCSET_DEFAULT_SIZES);

    for (int32 step = 1; step <= steps;)
    {
        Candidate *candidates = NULL;
        int candidate_count = 0, candidate_capacity = 0;
        HTAB *candidate_index;
        HTAB *query_proposals;
        HTAB *query_traversal_proposals = NULL;
        HTAB *projection_proposals = NULL;
        HTAB *query_evidence_table;
        HTAB *query_traversal_table;
        HTAB *projection_table = NULL;
        LaplaceQueryChannel *projection_channels = NULL;
        int projection_channel_count = 0;
        int pick = -1;

        MemoryContextReset(step_context);
        MemoryContextSwitchTo(step_context);
        CHECK_FOR_INTERRUPTS();
        candidate_index = new_id_index("forward candidate index", 128, step_context,
                                       sizeof(CandidateIndex));

        if (max_stride > 0)
        {
            int depth = input && step == 1 ? context_length : Min(context_length, max_stride);
            if (depth > 0)
            {
                ArrayType *tail = construct_array(context + context_length - depth, depth,
                                                  BYTEAOID, -1, false, TYPALIGN_INT);
                int count = 0;
                LaplaceContinuation *continuations =
                    laplace_trajectory_continuations_scoped(tail, true,
                                                            trajectory_scope, &count);
                for (int i = 0; i < count; ++i)
                {
                    candidate_add(&candidates, &candidate_count, &candidate_capacity,
                                  candidate_index, &continuations[i].id,
                                  continuations[i].occurrences,
                                  continuations[i].stride);
                    origin_inherit_sequence(origins, &continuations[i].id,
                                            context, context_length,
                                            continuations[i].stride, walk_context);
                }
                if (continuations)
                    pfree(continuations);
                pfree(tail);
            }
        }

        /* Q->K proposal is bounded per active occurrence. It may nominate a
         * candidate, but it is not the final evidence field used to elect it. */
        query_proposals = evidence_summaries(query_state, false, origins, step_context);

        if (input && (semantic_hop_limit < 0 || semantic_hops < semantic_hop_limit))
        {
            query_traversal_proposals = evidence_summaries(query_state, true, origins, step_context);
            HASH_SEQ_STATUS sequence;
            EvidenceEntry *nomination;
            hash_seq_init(&sequence, query_traversal_proposals);
            while ((nomination = hash_seq_search(&sequence)) != NULL)
            {
                if (!nomination->summary.has_positive ||
                    hash_search(route_seen, &nomination->id, HASH_FIND, NULL))
                    continue;
                candidate_add(&candidates, &candidate_count, &candidate_capacity,
                              candidate_index, &nomination->id, 0, 0);
            }
        }

        /* A caller-declared result relation is already an established output
         * contract. Reading that result is not an interpretation hop: a zero
         * routing budget must still execute the explicitly bound operation. */
        if (output_state)
        {
            projection_proposals = evidence_summaries(output_state, true, origins, step_context);
            HASH_SEQ_STATUS sequence;
            EvidenceEntry *projection;
            hash_seq_init(&sequence, projection_proposals);
            while ((projection = hash_seq_search(&sequence)) != NULL)
            {
                if (!projection->summary.has_positive)
                    continue;
                CandidateIndex *existing = hash_search(candidate_index,
                    &projection->id, HASH_FIND, NULL);
                if (!existing && context_contains_id(context, context_length, &projection->id))
                    continue;
                candidate_add(&candidates, &candidate_count, &candidate_capacity,
                              candidate_index, &projection->id, 0, 0);
            }
        }

        (void) query_proposals;
        (void) projection_proposals;
        if (candidate_count == 0)
        {
            exhausted = true;
            break;
        }

        /* K->V/evidence binding: after all bounded providers nominate the
         * candidate set, read EVERY exact stored typed cell between those
         * candidates and every active query occurrence. Negative/refuted cells
         * are retained here; proposal top-K cannot hide them. */
        ArrayType *candidate_ids = candidate_id_array(candidates, candidate_count);
        int query_channel_count = 0;
        LaplaceQueryChannel *query_channels =
            laplace_query_state_candidate_evidence(query_state, candidate_ids,
                                                   &query_channel_count, NULL);
        query_evidence_table = evidence_summaries_from_channels(
            query_channels, query_channel_count, false, origins, step_context);
        query_traversal_table = evidence_summaries_from_channels(
            query_channels, query_channel_count, true, origins, step_context);

        if (output_state)
        {
            projection_channels = laplace_query_state_candidate_evidence(
                output_state, candidate_ids, &projection_channel_count, NULL);
            projection_table = evidence_summaries_from_channels(
                projection_channels, projection_channel_count, true, origins, step_context);
        }
        pfree(candidate_ids);

        /* Completion provenance follows only typed semantic transitions. It is
         * folded from the exact candidate evidence for this iteration, before
         * structural ancestry is merged for trace/election accounting. Routed
         * identities therefore carry their prompt grounding into later hops
         * without letting a physical continuation manufacture semantic support. */
        if (cognition)
        {
            laplace_cognition_program_note_semantic_channels(
                cognition, query_channels, query_channel_count);
            if (projection_channels)
                laplace_cognition_program_note_semantic_channels(
                    cognition, projection_channels, projection_channel_count);
        }

        /* Bind candidate ancestry once, before either ROUTE or SELECT. Typed
         * relations and exact structural continuations therefore feed the same
         * trace/election provenance without becoming the semantic completion
         * certificate above. */
        propagate_candidate_origins(origins, candidate_index,
                                    query_channels, query_channel_count,
                                    walk_context);
        if (projection_channels)
            propagate_candidate_origins(origins, candidate_index,
                                        projection_channels,
                                        projection_channel_count,
                                        walk_context);

        int kept = 0;
        for (int i = 0; i < candidate_count; ++i)
        {
            EvidenceEntry *query = hash_search(query_evidence_table,
                                               &candidates[i].id, HASH_FIND, NULL);
            if (query)
                candidates[i].query = query->summary;
            EvidenceEntry *traversal = hash_search(query_traversal_table,
                                                   &candidates[i].id, HASH_FIND, NULL);
            if (traversal)
                candidates[i].query_traversal = traversal->summary;
            if (projection_table)
            {
                EvidenceEntry *projection = hash_search(projection_table,
                    &candidates[i].id, HASH_FIND, NULL);
                if (projection)
                    candidates[i].projection = projection->summary;
            }

            /* A graph-only result must survive exact positive typed evidence
             * after candidate adjudication. Physical continuations remain an
             * independently witnessed observation plane. */
            if (candidates[i].sequence_occurrences == 0 &&
                !candidates[i].projection.has_positive &&
                !(input && candidates[i].query_traversal.has_positive))
                continue;
            candidates[kept++] = candidates[i];
        }
        candidate_count = kept;
        if (candidate_count == 0)
        {
            exhausted = true;
            break;
        }

        qsort(candidates, (size_t) candidate_count, sizeof(Candidate), candidate_compare);
        int retained_channel_count = 0;
        (void) laplace_query_state_channels(query_state, &retained_channel_count);

        /* ROUTE and SELECT have different result types. A relation endpoint
         * can enter the working frontier without consuming an output ordinal
         * or inventing an observed continuation. Preserve every hypothesis in
         * this bounded provider frontier, rather than turn the best label into
         * an answer. All hypotheses extend each provider in one native batch. */
        int output_count = 0;
        for (int i = 0; i < candidate_count; ++i)
            if (candidates[i].sequence_occurrences > 0 ||
                candidates[i].projection.has_positive)
                output_count++;
        if (output_count == 0)
        {
            if (!input || semantic_hops >= semantic_hop_limit)
            {
                exhausted = true;
                break;
            }
            ++semantic_hops;
            if (cognition)
                laplace_cognition_program_note_route(cognition);
            ArrayType *routed = candidate_id_array(candidates, candidate_count);
            if (trace)
                for (int i = 0; i < candidate_count; ++i)
                    emit_result(result, step, &candidates[i], true, input,
                                candidate_count, context_length, retained_channel_count,
                                query_channel_count, true, semantic_hops, cognition);
            MemoryContextSwitchTo(walk_context);
            for (int i = 0; i < candidate_count; ++i)
                hash_search(route_seen, &candidates[i].id, HASH_ENTER, NULL);
            laplace_query_state_extend_batch(query_state, routed, NULL);
            if (output_state)
                laplace_query_state_extend_batch(output_state, routed, NULL);
            MemoryContextSwitchTo(step_context);
            pfree(routed);
            continue;
        }
        kept = 0;
        for (int i = 0; i < candidate_count; ++i)
            if (candidates[i].sequence_occurrences > 0 ||
                candidates[i].projection.has_positive)
                candidates[kept++] = candidates[i];
        candidate_count = kept;
        int limit = Min(candidate_count, top_k);
        if (spread == 0.0)
            pick = 0;
        else
        {
            double best = 0.0;
            for (int i = 0; i < limit; ++i)
            {
                double u = rng_uniform(&rng);
                /* Spread is applied to the ordinal election result, not to a
                 * fabricated cross-plane evidence scalar. */
                double key = -((double) i) / spread - log(-log(u));
                if (i == 0 || key > best)
                {
                    best = key;
                    pick = i;
                }
            }
        }
        if (pick < 0)
        {
            exhausted = true;
            break;
        }

        /* State transition precedes publication of the selected constituent.
         * The trace therefore reports the post-transition completion state and
         * the exact semantic-act id on the constituent that closes the program. */
        MemoryContextSwitchTo(walk_context);
        Datum selected = hash128_to_datum(&candidates[pick].id);
        context[context_length++] = selected;
        hash_search(route_seen, &candidates[pick].id, HASH_ENTER, NULL);
        OriginEntry *selected_origin = origin_get(origins, &candidates[pick].id, true);
        if (next_origin == INT_MAX)
            elog(ERROR, "forward execution: emitted occurrence ordinal overflow");
        selected_origin->occurrences = bms_add_member(selected_origin->occurrences,
                                                      next_origin++);
        laplace_query_state_extend(query_state, selected, NULL);
        if (output_state)
            laplace_query_state_extend(output_state, selected, NULL);

        if (trajectory_scope)
        {
            bool ordered = candidates[pick].stride > 0;
            laplace_trajectory_scope_select(trajectory_scope, selected, ordered);
            if (!ordered)
            {
                ArrayType *selection = construct_array(&selected, 1, BYTEAOID, -1,
                                                       false, TYPALIGN_INT);
                laplace_trajectory_scope_extend(trajectory_scope, selection);
                pfree(selection);
            }
        }
        if (cognition)
            laplace_cognition_program_note_emit(
                cognition, &candidates[pick].id, selected_origin->occurrences,
                candidates[pick].projection.has_positive ||
                candidates[pick].query_traversal.has_positive ||
                candidates[pick].query.has_positive);

        MemoryContextSwitchTo(step_context);
        emit_result(result, step, &candidates[pick], trace, input,
                    candidate_count, context_length, retained_channel_count,
                    query_channel_count, false, semantic_hops, cognition);
        ++step;

        if (cognition)
        {
            LaplaceCognitionProgramReceipt receipt;
            laplace_cognition_program_receipt(cognition, &receipt);
            /* Completion closes an open-ended cognition request. An explicit
             * output relation is a caller-declared bounded operation, so the
             * executor honors its requested step budget (or natural exhaustion)
             * instead of truncating the result chain at its first grounded row. */
            if (receipt.complete &&
                (!output_relations ||
                 ArrayGetNItems(ARR_NDIM(output_relations), ARR_DIMS(output_relations)) == 0))
                break;
        }
    }

    MemoryContextSwitchTo(walk_context);
    if (cognition)
    {
        LaplaceCognitionProgramReceipt receipt;
        laplace_cognition_program_receipt(cognition, &receipt);
        if (!receipt.complete)
            laplace_cognition_program_finalize(
                cognition,
                !exhausted && receipt.output_count >= steps
                    ? LAPLACE_COGNITION_BUDGET_EXHAUSTED
                    : LAPLACE_COGNITION_EXHAUSTED);
        if (trace)
        {
            MemoryContextSwitchTo(step_context);
            laplace_cognition_program_receipt(cognition, &receipt);
            emit_terminal(result, receipt.output_count + 1, input,
                          context_length, semantic_hops, cognition);
            MemoryContextSwitchTo(walk_context);
        }
    }

    MemoryContextDelete(step_context);
    laplace_query_state_destroy(&output_state);
    laplace_query_state_destroy(&query_state);
    laplace_cognition_program_destroy(&cognition);
    hash_destroy(route_seen);
    HASH_SEQ_STATUS origin_sequence;
    OriginEntry *origin;
    hash_seq_init(&origin_sequence, origins);
    while ((origin = hash_seq_search(&origin_sequence)) != NULL)
        bms_free(origin->occurrences);
    hash_destroy(origins);
    for (int i = 0; i < context_length; ++i)
        pfree(DatumGetPointer(context[i]));
    pfree(context);
    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_walk_continuations(PG_FUNCTION_ARGS)
{
    return walk_continuations(fcinfo, NULL, -1, false);
}

static Datum
forward_prompt(FunctionCallInfo fcinfo, bool trace)
{
    int hops;
    int fanout;

    if (PG_ARGISNULL(0) || VARSIZE_ANY_EXHDR(PG_GETARG_TEXT_PP(0)) == 0)
    {
        InitMaterializedSRF(fcinfo, 0);
        return (Datum) 0;
    }

    hops = PG_ARGISNULL(6) ? 2 : PG_GETARG_INT32(6);
    fanout = PG_ARGISNULL(7) ? 8 : PG_GETARG_INT32(7);
    if (hops < 0 || fanout < 0)
        ereport(ERROR,
                (errmsg("forward prompt: hops and fanout must not be negative")));

    LaplacePromptInput *input = laplace_prompt_input(PG_GETARG_TEXT_PP(0));
    HASHCTL ctl = {0};
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = CurrentMemoryContext;
    PromptFrontier frontier = {
        .owner = CurrentMemoryContext,
        .seen = hash_create("forward prompt supplemental operands", 128, &ctl,
                            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT),
    };

    ArrayIterator iterator = array_create_iterator(input->seeds, 0, NULL);
    Datum value;
    bool isnull;
    while (array_iterate(iterator, &value, &isnull))
    {
        if (isnull)
            continue;
        hash128_t id = datum_to_hash128(value);
        prompt_frontier_add(&frontier, &id);
    }
    array_free_iterator(iterator);

    if (!PG_ARGISNULL(8))
    {
        ArrayType *history = PG_GETARG_ARRAYTYPE_P(8);
        validate_id_array(history, "history", true);
        iterator = array_create_iterator(history, 0, NULL);
        while (array_iterate(iterator, &value, &isnull))
        {
            if (isnull)
                continue;
            hash128_t id = datum_to_hash128(value);
            prompt_frontier_add(&frontier, &id);
        }
        array_free_iterator(iterator);
    }

    LOCAL_FCINFO(walk_call, 12);
    InitFunctionCallInfoData(*walk_call, fcinfo->flinfo, 12, fcinfo->fncollation,
                             fcinfo->context, fcinfo->resultinfo);
    for (int i = 0; i < 12; ++i)
        walk_call->args[i].isnull = true;
    walk_call->args[0] = (NullableDatum) {PointerGetDatum(input->context), false};
    for (int i = 1; i <= 5; ++i)
        walk_call->args[i] = fcinfo->args[i];
    walk_call->args[6] = (NullableDatum) {
        makeArrayResult(frontier.ids, frontier.owner), false};
    walk_call->args[8] = (NullableDatum) {Int32GetDatum(fanout), false};
    walk_call->args[11] = fcinfo->args[9];

    return walk_continuations(walk_call, input, hops, trace);
}

Datum
pg_laplace_forward_prompt(PG_FUNCTION_ARGS)
{
    return forward_prompt(fcinfo, false);
}

Datum
pg_laplace_forward_trace(PG_FUNCTION_ARGS)
{
    return forward_prompt(fcinfo, true);
}
