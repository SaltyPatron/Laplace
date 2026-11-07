/* Membership (GIN) nominates trajectories that contain the context operands;
 * order comes only from the native matcher reading the packed ordered
 * occurrences, runs and separators included. The matcher elects the greatest
 * exact stride. */

#include "trajectory_continuations_internal.h"

static int
successor_cmp(const void *a, const void *b)
{
    const LaplaceContinuation *x = a, *y = b;
    if (x->occurrences > y->occurrences) return -1;
    if (x->occurrences < y->occurrences) return 1;
    return memcmp(&x->id, &y->id, sizeof(hash128_t));
}

static void
match_member_trajectory(Datum physicality, Datum entity, Datum geometry, void *context)
{
    MembershipMatcher *match = context;
    (void)physicality; (void)entity;
    MemoryContext previous = MemoryContextSwitchTo(match->row);
    bytea *wkb = DatumGetByteaP(OidFunctionCall1(match->as_binary, geometry));
    uint32 npoints;
    const unsigned char *points = laplace_trajectory_wkb_points(wkb, &npoints);
    if (trajectory_match_suffixes(match->matcher, points, npoints,
                                  record_successor, match->state) != 0)
        elog(ERROR, "trajectory_continuations: invalid packed trajectory");
    MemoryContextSwitchTo(previous);
    MemoryContextReset(match->row);
}

/* Route is part of the key: a successor and a co-occurrence reaching the same
 * target keep separate multiplicity and gap state into COUPLE. */
typedef struct StructuralKey
{
    hash128_t source;
    hash128_t target;
    uint32 relation;
} StructuralKey;

typedef struct StructuralEntry
{
    StructuralKey key;
    uint32 relation_mask;
    int64 occurrences;
    uint64 nearest_gap;
} StructuralEntry;

static void
structural_add(HTAB *crossings, const hash128_t *source, const hash128_t *target,
               uint32 relation, int64 occurrences, uint64 gap)
{
    StructuralKey key;
    bool found;
    MemSet(&key, 0, sizeof(key));
    key.source = *source;
    key.target = *target;
    key.relation = relation;
    StructuralEntry *entry = hash_search(crossings, &key, HASH_ENTER, &found);
    if (!found)
    {
        entry->relation_mask = relation;
        entry->occurrences = 0;
        entry->nearest_gap = UINT64CONST(0xFFFFFFFFFFFFFFFF);
    }
    if (entry->relation_mask != relation)
        elog(ERROR, "trajectory structural crossings: route identity changed inside one cell");
    if (occurrences < 0 || entry->occurrences > PG_INT64_MAX - occurrences)
        ereport(ERROR, (errmsg("trajectory structural crossings: occurrence count overflow")));
    entry->occurrences += occurrences;
    if (gap < entry->nearest_gap) entry->nearest_gap = gap;
}

static int
structural_cmp(const void *left, const void *right)
{
    const LaplaceStructuralCandidate *a = left, *b = right;
    int cmp = memcmp(&a->source, &b->source, sizeof(hash128_t));
    if (cmp != 0) return cmp;
    cmp = memcmp(&a->id, &b->id, sizeof(hash128_t));
    if (cmp != 0) return cmp;
    if (a->relation_mask < b->relation_mask) return -1;
    if (a->relation_mask > b->relation_mask) return 1;
    return 0;
}

LaplaceStructuralCandidate *
laplace_trajectory_structural_candidates(LaplaceTrajectoryScope *scope,
                                         ArrayType *sources,
                                         uint32 relation_mask,
                                         int *count)
{
    const uint32 supported = LAPLACE_STRUCTURAL_CONTAINER |
                             LAPLACE_STRUCTURAL_CONSTITUENT |
                             LAPLACE_STRUCTURAL_PREDECESSOR |
                             LAPLACE_STRUCTURAL_SUCCESSOR |
                             LAPLACE_STRUCTURAL_COOCCUR;
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext work = AllocSetContextCreate(owner, "trajectory structural crossings",
                                               ALLOCSET_DEFAULT_SIZES);
    MemoryContext previous = MemoryContextSwitchTo(work);
    Datum *values = NULL;
    bool *nulls = NULL;
    int source_count = 0;
    HASHCTL ctl = {0};
    LaplaceStructuralCandidate *result;

    if (!count)
        elog(ERROR, "trajectory structural crossings: count output is required");
    *count = 0;
    if (!scope || !sources || relation_mask == 0)
    {
        MemoryContextSwitchTo(owner);
        result = palloc(sizeof(*result));
        MemoryContextDelete(work);
        return result;
    }
    if (relation_mask & ~supported)
        ereport(ERROR, (errmsg("trajectory structural crossings: unsupported relation mask %u",
                               relation_mask)));
    if (ARR_NDIM(sources) > 1 || ARR_ELEMTYPE(sources) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory structural crossings: sources must be a 1-D bytea array")));

    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = work;
    HTAB *source_ids = hash_create("trajectory structural sources", 128, &ctl,
                                   HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    deconstruct_array(sources, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &source_count);
    for (int i = 0; i < source_count; ++i)
    {
        if (nulls[i])
            ereport(ERROR, (errmsg("trajectory structural crossings: sources contain NULL")));
        bytea *id = DatumGetByteaPP(values[i]);
        if (VARSIZE_ANY_EXHDR(id) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("trajectory structural crossings: source ids must be 16 bytes")));
        hash_search(source_ids, VARDATA_ANY(id), HASH_ENTER, NULL);
    }
    if (source_count > 0)
    {
        pfree(values);
        pfree(nulls);
    }

    ctl.keysize = sizeof(StructuralKey);
    ctl.entrysize = sizeof(StructuralEntry);
    HTAB *crossings = hash_create("trajectory structural candidates", 256, &ctl,
                                  HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

    for (ScopedTrajectory *trajectory = scope->trajectories;
         trajectory; trajectory = trajectory->next)
    {
        uint32 npoints = 0;
        const unsigned char *raw = laplace_trajectory_wkb_points(trajectory->wkb, &npoints);
        if (npoints == 0) continue;
        if ((Size)npoints > MaxAllocSize / (4 * sizeof(double)))
            ereport(ERROR, (errmsg("trajectory structural crossings: packed trajectory exceeds allocation capacity")));
        Size bytes = (Size)npoints * 4 * sizeof(double);
        double *aligned = palloc(bytes);
        memcpy(aligned, raw, bytes);
        size_t logical = 0;
        if (trajectory_constituent_count(aligned, npoints, &logical) != 0 ||
            logical > INT_MAX || logical > MaxAllocSize / sizeof(hash128_t))
            elog(ERROR, "trajectory structural crossings: invalid or oversized trajectory");
        hash128_t *items = palloc(sizeof(*items) * Max(logical, (size_t)1));
        int decoded = trajectory_constituents(aligned, npoints, items, logical);
        if (decoded < 0 || (size_t)decoded != logical)
            elog(ERROR, "trajectory structural crossings: constituent decode failed");

        if ((relation_mask & LAPLACE_STRUCTURAL_CONSTITUENT) &&
            hash_search(source_ids, &trajectory->entity, HASH_FIND, NULL))
            for (size_t i = 0; i < logical; ++i)
                structural_add(crossings, &trajectory->entity, &items[i],
                               LAPLACE_STRUCTURAL_CONSTITUENT, 1, 0);

        for (size_t i = 0; i < logical; ++i)
        {
            const hash128_t *source = &items[i];
            if (!hash_search(source_ids, source, HASH_FIND, NULL)) continue;
            if (relation_mask & LAPLACE_STRUCTURAL_CONTAINER)
                structural_add(crossings, source, &trajectory->entity,
                               LAPLACE_STRUCTURAL_CONTAINER, 1, 0);
            if ((relation_mask & LAPLACE_STRUCTURAL_PREDECESSOR) && i > 0)
                structural_add(crossings, source, &items[i - 1],
                               LAPLACE_STRUCTURAL_PREDECESSOR, 1, 1);
            if ((relation_mask & LAPLACE_STRUCTURAL_SUCCESSOR) && i + 1 < logical)
                structural_add(crossings, source, &items[i + 1],
                               LAPLACE_STRUCTURAL_SUCCESSOR, 1, 1);
            if (relation_mask & LAPLACE_STRUCTURAL_COOCCUR)
            {
                for (size_t j = 0; j < logical; ++j)
                {
                    if (j == i) continue;
                    uint64 gap = (uint64)(j > i ? j - i : i - j);
                    structural_add(crossings, source, &items[j],
                                   LAPLACE_STRUCTURAL_COOCCUR, 1, gap);
                }
            }
            if ((i & 255) == 0) CHECK_FOR_INTERRUPTS();
        }
        pfree(items);
        pfree(aligned);
        CHECK_FOR_INTERRUPTS();
    }

    long entries = hash_get_num_entries(crossings);
    if (entries > INT_MAX || (uint64)entries > MaxAllocSize / sizeof(*result))
        ereport(ERROR, (errmsg("trajectory structural crossings: candidate set exceeds allocation capacity")));
    MemoryContextSwitchTo(owner);
    result = palloc(sizeof(*result) * Max(entries, 1));
    HASH_SEQ_STATUS sequence;
    StructuralEntry *entry;
    hash_seq_init(&sequence, crossings);
    while ((entry = hash_seq_search(&sequence)) != NULL)
        result[(*count)++] = (LaplaceStructuralCandidate){
            .source = entry->key.source,
            .id = entry->key.target,
            .relation_mask = entry->relation_mask,
            .occurrences = entry->occurrences,
            .nearest_gap = entry->nearest_gap
        };
    qsort(result, *count, sizeof(*result), structural_cmp);
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
    MemoryContextSwitchTo(owner);
    return result;
}

LaplaceContinuation *
laplace_trajectory_continuations(ArrayType *context_array, bool suffix_backoff, int *count)
{
    return laplace_trajectory_continuations_scoped(context_array, suffix_backoff, NULL, count);
}

LaplaceContinuation *
laplace_trajectory_continuations_scoped(ArrayType *context_array, bool suffix_backoff,
                                       LaplaceTrajectoryScope *scope, int *count)
{
    MemoryContext owner = CurrentMemoryContext;
    MemoryContext work = AllocSetContextCreate(owner, "trajectory suffix operands",
                                               ALLOCSET_DEFAULT_SIZES);
    Datum *ids;
    bool *nulls;
    int n_context;
    LaplaceContinuation *ordered;
    *count = 0;
    if (ARR_NDIM(context_array) > 1 || ARR_ELEMTYPE(context_array) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory_continuations: context must be a 1-D bytea array")));
    MemoryContextSwitchTo(work);
    deconstruct_array(context_array, BYTEAOID, -1, false, TYPALIGN_INT,
                      &ids, &nulls, &n_context);
    if (n_context < 1)
        ereport(ERROR, (errmsg("trajectory_continuations: context must not be empty")));
    if ((Size) n_context > MaxAllocSize / sizeof(hash128_t))
        ereport(ERROR, (errmsg("trajectory_continuations: context exceeds allocation capacity")));
    hash128_t *context = palloc((Size) n_context * sizeof(hash128_t));
    for (int i = 0; i < n_context; ++i)
    {
        if (nulls[i])
            ereport(ERROR, (errmsg("trajectory_continuations: context contains NULL")));
        bytea *id = DatumGetByteaPP(ids[i]);
        if (VARSIZE_ANY_EXHDR(id) != sizeof(hash128_t))
            ereport(ERROR, (errmsg("trajectory_continuations: context ids must be 16 bytes")));
        memcpy(context + i, VARDATA_ANY(id), sizeof(hash128_t));
    }
    MatcherCleanup *cleanup = palloc0(sizeof(*cleanup));
    cleanup->matcher = trajectory_suffix_matcher_create(context, n_context,
                                                        suffix_backoff ? 1 : n_context);
    if (!cleanup->matcher)
        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY),
                        errmsg("trajectory_continuations: allocating native suffix matcher failed")));
    cleanup->callback.func = free_matcher;
    cleanup->callback.arg = cleanup;
    MemoryContextRegisterResetCallback(work, &cleanup->callback);
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplaceContinuation);
    ctl.hcxt = work;
    SuccessorState state = {
        .successors = hash_create("trajectory successors", 256, &ctl,
            HASH_ELEM | HASH_BLOBS | HASH_CONTEXT), .scope = scope
    };
    if (scope && scope->advancing)
    {
        size_t retained = 0;
        for (size_t i = 0; i < scope->position_count; ++i)
        {
            ScopedPosition position = scope->positions[i];
            ScopedTrajectory *entry = position.trajectory;
            if (!entry->ordinal_index)
            {
                uint32 npoints;
                const unsigned char *points = laplace_trajectory_wkb_points(entry->wkb, &npoints);
                entry->ordinal_index = trajectory_ordinal_index_create(points, npoints);
                if (!entry->ordinal_index)
                    elog(ERROR, "trajectory scope: ordinal index allocation failed");
            }
            int result_code = trajectory_ordinal_index_read(entry->ordinal_index,
                position.ordinal, &position.successor, NULL);
            if (result_code < 0) elog(ERROR, "trajectory scope: invalid ordinal read");
            if (result_code == 1) continue;
            position.stride = n_context;
            scope->positions[retained++] = position;
            record_successor(&state, position.ordinal, n_context, &position.successor);
        }
        scope->position_count = retained;
    }
    else if (scope)
    {
        scope->position_count = 0;
        for (ScopedTrajectory *entry = scope->trajectories; entry; entry = entry->next)
        {
            state.trajectory = entry;
            uint32 npoints;
            const unsigned char *points = laplace_trajectory_wkb_points(entry->wkb, &npoints);
            if (trajectory_match_suffixes(cleanup->matcher, points, npoints,
                                           record_successor, &state) != 0)
                elog(ERROR, "trajectory_continuations: invalid packed trajectory");
            CHECK_FOR_INTERRUPTS();
        }
    }
    MembershipMatcher match = {.matcher = cleanup->matcher, .state = &state};
    if (!scope)
    {
        Oid physicalities = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
        Oid geometry = get_atttype(physicalities, get_attnum(physicalities, "trajectory"));
        match.as_binary = LookupFuncName(list_make2(makeString("public"), makeString("st_asbinary")),
                                         1, &geometry, false);
        match.row = AllocSetContextCreate(work, "trajectory membership match", ALLOCSET_SMALL_SIZES);
    }
    /* Unscoped proposal. Membership of a length-k suffix nominates every
     * trajectory a longer suffix could match, and the matcher evaluates those
     * longer strides too, so the elected stride and counts are complete. With
     * suffix_backoff a miss halves the probe length (rounding up): at most
     * ceil(log2(n_context))+1 index probes, and the widely posted short
     * suffixes are read only when every longer one misses. */
    int probe_length = n_context;
    while (!scope)
    {
        state.stride = (size_t) probe_length;
        ArrayType *probe = probe_length == n_context ? context_array
            : construct_array(ids + n_context - probe_length, probe_length,
                              BYTEAOID, -1, false, TYPALIGN_INT);
        laplace_content_membership_read(probe, true, match_member_trajectory, &match);
        if (probe != context_array) pfree(probe);
        if (!suffix_backoff || probe_length == 1 ||
            hash_get_num_entries(state.successors) > 0) break;
        probe_length = probe_length / 2 + probe_length % 2;
    }

    long entries = hash_get_num_entries(state.successors);
    if (entries > INT_MAX || (uint64) entries > MaxAllocSize / sizeof(*ordered))
        ereport(ERROR, (errmsg("trajectory_continuations: successor set exceeds allocation capacity")));
    MemoryContextSwitchTo(owner);
    ordered = palloc(sizeof(*ordered) * Max(entries, 1));
    HASH_SEQ_STATUS sequence;
    LaplaceContinuation *entry;
    hash_seq_init(&sequence, state.successors);
    while ((entry = hash_seq_search(&sequence)) != NULL)
        if (entry->stride == (int) state.stride) ordered[(*count)++] = *entry;
    qsort(ordered, *count, sizeof(*ordered), successor_cmp);
    MemoryContextDelete(work);
    return ordered;
}

PG_FUNCTION_INFO_V1(pg_laplace_trajectory_continuations);
Datum
pg_laplace_trajectory_continuations(PG_FUNCTION_ARGS)
{
    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("trajectory_continuations: context must not be NULL")));
    bool bounded = !PG_ARGISNULL(1);
    int topk = bounded ? PG_GETARG_INT32(1) : 0;
    if (topk < 0)
        ereport(ERROR, (errmsg("trajectory_continuations: topk must not be negative")));
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    int count;
    LaplaceTrajectoryScope *scope = NULL;
    if (PG_NARGS() > 2 && !PG_ARGISNULL(2))
    {
        scope = laplace_trajectory_scope_create();
        laplace_trajectory_scope_extend(scope, PG_GETARG_ARRAYTYPE_P(2));
    }
    LaplaceContinuation *successors = laplace_trajectory_continuations_scoped(
        PG_GETARG_ARRAYTYPE_P(0), false, scope, &count);
    if (bounded && count > topk) count = topk;
    for (int i = 0; i < count; ++i)
    {
        Datum values[3] = {hash128_to_datum(&successors[i].id), (Datum) 0,
                           Int64GetDatum(successors[i].occurrences)};
        bool nulls[3] = {false, true, false};
        tuplestore_putvalues(result->setResult, result->setDesc, values, nulls);
        pfree(DatumGetPointer(values[0]));
    }
    pfree(successors);
    return (Datum) 0;
}
