/*
 * trajectory_generate_election.c — Candidate list management, geometry
 * response summaries, meeting specificity sharing sort, and election ordering.
 */
#include "trajectory_generate_internal.h"

static uint64
splitmix64(uint64 *state)
{
    uint64 z = (*state += UINT64CONST(0x9E3779B97F4A7C15));
    z = (z ^ (z >> 30)) * UINT64CONST(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)) * UINT64CONST(0x94D049BB133111EB);
    return z ^ (z >> 31);
}

double
laplace_tg_rng_uniform(uint64 *state)
{
    return ((double) (splitmix64(state) >> 11) + 0.5) *
           (1.0 / 9007199254740992.0);
}

bool
laplace_tg_context_contains_id(Datum *context, int count, const hash128_t *id)
{
    for (int i = 0; i < count; ++i)
    {
        if (memcmp(VARDATA_ANY(DatumGetByteaPP(context[i])),
                   id, sizeof(hash128_t)) == 0)
            return true;
    }
    return false;
}

int
laplace_tg_candidate_add(Candidate **items, int *count, int *capacity, HTAB *index,
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
        .geometry = {0},
    };
    return (*count)++;
}

ArrayType *
laplace_tg_candidate_id_array(const Candidate *candidates, int count)
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

bool
laplace_tg_candidate_is_intent_result(const Candidate *candidate,
                                      const LaplacePromptIntent *intent)
{
    return intent && !intent->ambiguous && candidate && candidate->intent_result;
}

bool
laplace_tg_candidate_can_output(const Candidate *candidate,
                                const LaplacePromptIntent *intent)
{
    if (intent && intent->explicit_invocation)
        return laplace_tg_candidate_is_intent_result(candidate, intent);

    return candidate->sequence_occurrences > 0 ||
           candidate->projection.has_positive ||
           laplace_tg_candidate_is_intent_result(candidate, intent);
}

bool
laplace_tg_relation_allowed_by_output_scope(const hash128_t *relation, ArrayType *scope)
{
    ArrayIterator iterator;
    Datum value;
    bool isnull, found = false;
    if (!scope || ArrayGetNItems(ARR_NDIM(scope), ARR_DIMS(scope)) == 0) return true;
    iterator = array_create_iterator(scope, 0, NULL);
    while (array_iterate(iterator, &value, &isnull))
    {
        hash128_t id;
        if (isnull) continue;
        id = datum_to_hash128(value);
        if (hash128_eq(&id, relation)) { found = true; break; }
    }
    array_free_iterator(iterator);
    return found;
}

HTAB *
laplace_tg_geometry_summaries(const LaplacePromptIntent *intent, MemoryContext owner)
{
    HTAB *summaries = laplace_tg_new_id_index(
        "forward geometry response summaries",
        intent ? Max(intent->geometry_count, 16) : 16,
        owner, sizeof(GeometryEntry));
    if (!intent)
        return summaries;

    for (int i = 0; i < intent->geometry_count; ++i)
    {
        const LaplacePromptGeometryCandidate *candidate = &intent->geometry[i];
        bool found;
        GeometryEntry *entry = hash_search(
            summaries, &candidate->id, HASH_ENTER, &found);
        if (!found)
            MemSet(&entry->summary, 0, sizeof(entry->summary));

        switch (candidate->plane)
        {
            case LAPLACE_PROMPT_GEOMETRY_ANGULAR:
                if (!entry->summary.has_angular ||
                    candidate->distance < entry->summary.angular_distance ||
                    (candidate->distance == entry->summary.angular_distance &&
                     candidate->rank < entry->summary.angular_rank))
                {
                    entry->summary.has_angular = true;
                    entry->summary.angular_rank = candidate->rank;
                    entry->summary.angular_distance = candidate->distance;
                }
                break;
            case LAPLACE_PROMPT_GEOMETRY_FRECHET:
                if (!entry->summary.has_frechet ||
                    candidate->distance < entry->summary.frechet_distance ||
                    (candidate->distance == entry->summary.frechet_distance &&
                     candidate->rank < entry->summary.frechet_rank))
                {
                    entry->summary.has_frechet = true;
                    entry->summary.frechet_rank = candidate->rank;
                    entry->summary.frechet_distance = candidate->distance;
                }
                break;
            case LAPLACE_PROMPT_GEOMETRY_HILBERT:
                if (!entry->summary.has_hilbert ||
                    memcmp(candidate->hilbert_delta,
                           entry->summary.hilbert_delta, 16) < 0 ||
                    (memcmp(candidate->hilbert_delta,
                            entry->summary.hilbert_delta, 16) == 0 &&
                     candidate->rank < entry->summary.hilbert_rank))
                {
                    entry->summary.has_hilbert = true;
                    entry->summary.hilbert_rank = candidate->rank;
                    memcpy(entry->summary.hilbert_delta,
                           candidate->hilbert_delta, 16);
                }
                break;
            default:
                elog(ERROR, "forward execution: unknown geometry response plane");
        }
    }
    return summaries;
}

int
laplace_tg_geometry_summary_compare(const GeometrySummary *a, const GeometrySummary *b)
{
    if (a->has_angular != b->has_angular)
        return a->has_angular ? 1 : -1;
    if (a->has_angular)
    {
        if (a->angular_distance != b->angular_distance)
            return a->angular_distance < b->angular_distance ? 1 : -1;
        if (a->angular_rank != b->angular_rank)
            return a->angular_rank < b->angular_rank ? 1 : -1;
    }

    if (a->has_frechet && b->has_frechet)
    {
        if (a->frechet_distance != b->frechet_distance)
            return a->frechet_distance < b->frechet_distance ? 1 : -1;
        if (a->frechet_rank != b->frechet_rank)
            return a->frechet_rank < b->frechet_rank ? 1 : -1;
    }

    if (a->has_hilbert != b->has_hilbert)
        return a->has_hilbert ? 1 : -1;
    if (a->has_hilbert)
    {
        int cmp = memcmp(a->hilbert_delta, b->hilbert_delta, 16);
        if (cmp != 0) return cmp < 0 ? 1 : -1;
        if (a->hilbert_rank != b->hilbert_rank)
            return a->hilbert_rank < b->hilbert_rank ? 1 : -1;
    }
    return 0;
}

bool
laplace_tg_geometry_summary_present(const GeometrySummary *summary)
{
    return summary &&
           (summary->has_angular || summary->has_frechet || summary->has_hilbert);
}

#define SHARING_WINDOW (laplace_firmware_default()->sharing_window)
#define SHARING_CAP (laplace_firmware_default()->sharing_cap)

typedef struct SharingKey
{
    hash128_t object;
    hash128_t type;
} SharingKey;

typedef struct SharingEntry
{
    SharingKey key;
    int64 count;
} SharingEntry;

static bool
summary_grounded(const EvidenceSummary *s)
{
    return s->has_positive &&
           (s->positive_required_occurrences > 0 ||
            s->positive_covered_occurrences > 0);
}

static const LaplaceQueryChannel *
candidate_support(const Candidate *candidate)
{
    if (summary_grounded(&candidate->query_traversal)) return &candidate->query_traversal.positive;
    if (summary_grounded(&candidate->query)) return &candidate->query.positive;
    if (summary_grounded(&candidate->projection)) return &candidate->projection.positive;
    return NULL;
}

int32
laplace_tg_candidate_grounded(const Candidate *candidate)
{
    return Max(candidate->projection.positive_required_occurrences,
               Max(candidate->query_traversal.positive_required_occurrences,
                   candidate->query.positive_required_occurrences));
}

static SharingKey
candidate_sharing_key(const Candidate *candidate, const LaplaceQueryChannel *support)
{
    SharingKey key;
    MemSet(&key, 0, sizeof(key));
    key.object = support->outbound ? candidate->id : support->anchor;
    key.type = support->relation;
    return key;
}

static void
receive_sharing(const LaplaceConsensusRow *row, void *context)
{
    SharingKey key;
    MemSet(&key, 0, sizeof(key));
    key.object = row->object;
    key.type = row->type;
    SharingEntry *entry = hash_search((HTAB *) context, &key, HASH_FIND, NULL);
    if (entry) entry->count++;
}

static bool
sharing_cutoff(const LaplaceConsensusRow *row, void *context)
{
    SharingKey key;
    MemSet(&key, 0, sizeof(key));
    key.object = row->object;
    key.type = row->type;
    SharingEntry *entry = hash_search((HTAB *) context, &key, HASH_FIND, NULL);
    return entry && entry->count >= SHARING_CAP;
}

void
laplace_tg_order_leading_by_sharing(Candidate *candidates, int count)
{
    int group = 0;
    if (count < 2 || laplace_tg_candidate_grounded(&candidates[0]) <= 0)
        return;
    while (group < count && group < SHARING_WINDOW &&
           laplace_tg_candidate_grounded(&candidates[group]) == laplace_tg_candidate_grounded(&candidates[0]))
        ++group;
    if (group < 2)
        return;

    HASHCTL ctl = {0};
    ctl.keysize = sizeof(SharingKey);
    ctl.entrysize = sizeof(SharingEntry);
    ctl.hcxt = CurrentMemoryContext;
    HTAB *sharing = hash_create("forward meeting sharing", group * 2, &ctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    hash128_t *objects = palloc(sizeof(hash128_t) * group);
    hash128_t *types = palloc(sizeof(hash128_t) * group);
    int object_count = 0, type_count = 0;
    int64 *shared = palloc0(sizeof(int64) * group);
    for (int i = 0; i < group; ++i)
    {
        const LaplaceQueryChannel *support = candidate_support(&candidates[i]);
        bool found;
        if (!support) continue;
        SharingKey key = candidate_sharing_key(&candidates[i], support);
        SharingEntry *entry = hash_search(sharing, &key, HASH_ENTER, &found);
        if (found) continue;
        entry->count = 0;
        objects[object_count++] = key.object;
        bool seen_type = false;
        for (int t = 0; t < type_count && !seen_type; ++t)
            seen_type = hash128_eq(&types[t], &key.type);
        if (!seen_type) types[type_count++] = key.type;
    }
    if (object_count > 0)
    {
        ArrayType *object_ids = hash128_array_from_ids(objects, object_count);
        ArrayType *type_ids = hash128_array_from_ids(types, type_count);
        laplace_consensus_ranked_candidates(NULL, object_ids, type_ids, false, false,
                                            receive_sharing, sharing_cutoff, sharing, NULL);
        pfree(object_ids);
        pfree(type_ids);
    }
    for (int i = 0; i < group; ++i)
    {
        const LaplaceQueryChannel *support = candidate_support(&candidates[i]);
        shared[i] = SHARING_CAP;
        if (!support) continue;
        SharingKey key = candidate_sharing_key(&candidates[i], support);
        SharingEntry *entry = hash_search(sharing, &key, HASH_FIND, NULL);
        if (entry) shared[i] = entry->count;
    }
    for (int i = 1; i < group; ++i)
    {
        Candidate held = candidates[i];
        int64 held_shared = shared[i];
        int j = i - 1;
        while (j >= 0 && shared[j] > held_shared)
        {
            candidates[j + 1] = candidates[j];
            shared[j + 1] = shared[j];
            --j;
        }
        candidates[j + 1] = held;
        shared[j + 1] = held_shared;
    }
    pfree(shared);
    pfree(objects);
    pfree(types);
    hash_destroy(sharing);
}

int
laplace_tg_candidate_compare(const void *left, const void *right)
{
    const Candidate *a = left;
    const Candidate *b = right;
    int cmp;

    cmp = laplace_tg_positive_summary_compare(&a->projection, &b->projection);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    if (summary_grounded(&a->query_traversal) || summary_grounded(&b->query_traversal))
    {
        if (summary_grounded(&a->query_traversal) != summary_grounded(&b->query_traversal))
            return summary_grounded(&a->query_traversal) ? -1 : 1;
        cmp = laplace_tg_positive_summary_compare(&a->query_traversal, &b->query_traversal);
        if (cmp != 0) return cmp > 0 ? -1 : 1;
    }
    if (summary_grounded(&a->query) || summary_grounded(&b->query))
    {
        if (summary_grounded(&a->query) != summary_grounded(&b->query))
            return summary_grounded(&a->query) ? -1 : 1;
        cmp = laplace_tg_positive_summary_compare(&a->query, &b->query);
        if (cmp != 0) return cmp > 0 ? -1 : 1;
    }
    if (a->stride != b->stride)
        return a->stride > b->stride ? -1 : 1;
    if (a->sequence_occurrences != b->sequence_occurrences)
        return a->sequence_occurrences > b->sequence_occurrences ? -1 : 1;
    cmp = laplace_tg_positive_summary_compare(&a->query_traversal, &b->query_traversal);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = laplace_tg_positive_summary_compare(&a->query, &b->query);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = laplace_tg_opposition_summary_compare(&a->query, &b->query);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = laplace_tg_opposition_summary_compare(&a->query_traversal, &b->query_traversal);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = laplace_tg_opposition_summary_compare(&a->projection, &b->projection);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    cmp = laplace_tg_geometry_summary_compare(&a->geometry, &b->geometry);
    if (cmp != 0) return cmp > 0 ? -1 : 1;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}
