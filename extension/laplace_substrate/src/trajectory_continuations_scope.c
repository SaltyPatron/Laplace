#include "trajectory_continuations_internal.h"

LaplaceTrajectoryScope *
laplace_trajectory_scope_create(void)
{
    LaplaceTrajectoryScope *scope = palloc0(sizeof(*scope));
    HASHCTL ctl = {0};
    scope->owner = CurrentMemoryContext;
    Oid physicalities = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
    Oid geometry = get_atttype(physicalities, get_attnum(physicalities, "trajectory"));
    scope->as_binary = LookupFuncName(list_make2(makeString("public"), makeString("st_asbinary")),
                                      1, &geometry, false);
    ctl.keysize = ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = scope->owner;
    scope->operands = hash_create("observed trajectory operands", 128, &ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    scope->roots = hash_create("observed trajectory roots", 256, &ctl,
                              HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ctl.entrysize = sizeof(ScopedPhysicality);
    scope->physicalities = hash_create("observed physicality identities", 256, &ctl,
                                      HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    return scope;
}

static void
scope_release_index(void *argument)
{
    ScopedTrajectory *entry = argument;
    trajectory_ordinal_index_free(entry->ordinal_index);
}

static ScopedTrajectory *
scope_keep_trajectory(Datum physicality, Datum entity, Datum geometry, LaplaceTrajectoryScope *scope)
{
    bytea *physicality_id = DatumGetByteaPP(physicality);
    bytea *entity_id = DatumGetByteaPP(entity);
    bool found;
    if (VARSIZE_ANY_EXHDR(physicality_id) != sizeof(hash128_t) ||
        VARSIZE_ANY_EXHDR(entity_id) != sizeof(hash128_t))
        elog(ERROR, "trajectory scope: malformed stored physicality identity");
    ScopedPhysicality *known = hash_search(scope->physicalities,
        VARDATA_ANY(physicality_id), HASH_ENTER, &found);
    if (found) return known->trajectory;
    MemoryContext previous = MemoryContextSwitchTo(scope->owner);
    ScopedTrajectory *entry = palloc0(sizeof(*entry));
    known->trajectory = entry;
    memcpy(&entry->physicality, VARDATA_ANY(physicality_id), sizeof(hash128_t));
    memcpy(&entry->entity, VARDATA_ANY(entity_id), sizeof(hash128_t));
    entry->wkb = DatumGetByteaP(OidFunctionCall1(scope->as_binary, geometry));
    entry->next = scope->trajectories;
    scope->trajectories = entry;
    entry->cleanup.func = scope_release_index;
    entry->cleanup.arg = entry;
    MemoryContextRegisterResetCallback(scope->owner, &entry->cleanup);
    MemoryContextSwitchTo(previous);
    return entry;
}

static void
scope_retain_trajectory(Datum physicality, Datum entity, Datum geometry, void *context)
{
    ScopedTrajectory *entry = scope_keep_trajectory(physicality, entity, geometry, context);
    entry->structural = true;
}

static void
scope_add_id(HTAB *known, ArrayBuildState **batch, Datum value, MemoryContext work)
{
    bytea *id = DatumGetByteaPP(value);
    bool found;
    if (VARSIZE_ANY_EXHDR(id) != sizeof(hash128_t))
        ereport(ERROR, (errmsg("trajectory scope: ids must be 16 bytes")));
    hash_search(known, VARDATA_ANY(id), HASH_ENTER, &found);
    if (!found) *batch = accumArrayResult(*batch, value, false, BYTEAOID, work);
}

typedef struct BindingReceiver
{
    LaplaceTrajectoryScope *scope;
    ArrayBuildState **roots;
    MemoryContext work;
} BindingReceiver;

static void
receive_binding(int ordinal, int16 role, const LaplaceObservation *observation, void *context)
{
    BindingReceiver *receiver = context;
    MemoryContext previous = MemoryContextSwitchTo(receiver->work);
    bool found;
    (void) ordinal;
    (void) role;
    if (!observation->context_null)
    {
        hash_search(receiver->scope->roots, &observation->context, HASH_ENTER, &found);
        if (!found)
            *receiver->roots = accumArrayResult(*receiver->roots,
                hash128_to_datum(&observation->context), false, BYTEAOID, receiver->work);
    }
    MemoryContextSwitchTo(previous);
}

/* Each new operand's observation context roots are read once and each new
 * root's trajectory is loaded once as packed WKB under the caller's snapshot.
 * An operand with no observation adds nothing. A relation object is not an
 * observation context, so routing to one does not add its trajectory; typed
 * output objects are proposed by the caller's own operation. */
void
laplace_trajectory_scope_extend(LaplaceTrajectoryScope *scope, ArrayType *operands)
{
    MemoryContext work, previous;
    ArrayBuildState *missing = NULL, *roots = NULL;
    Datum *ids;
    bool *nulls;
    int count;
    bool spi_top = false;
    if (ARR_NDIM(operands) > 1 || ARR_ELEMTYPE(operands) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory scope: operands must be a 1-D bytea array")));
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "trajectory scope: SPI_connect failed");
    work = AllocSetContextCreate(CurrentMemoryContext, "trajectory scope batch",
                                 ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(work);
    deconstruct_array(operands, BYTEAOID, -1, false, TYPALIGN_INT, &ids, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        if (nulls[i]) continue;
        scope_add_id(scope->operands, &missing, ids[i], work);
        scope_add_id(scope->roots, &roots, ids[i], work);
    }
    if (missing)
    {
        BindingReceiver receiver = {
            .scope = scope, .roots = &roots, .work = work
        };
        laplace_observation_read(DatumGetArrayTypeP(makeArrayResult(missing, work)),
            NULL, NULL, 3, receive_binding, &receiver);
    }
    if (roots)
        laplace_content_trajectory_read(DatumGetArrayTypeP(makeArrayResult(roots, work)),
                                        scope_retain_trajectory, scope);
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
    laplace_spi_finish(spi_top);
}

void
laplace_trajectory_scope_extend_containing(LaplaceTrajectoryScope *scope, ArrayType *members)
{
    if (ARR_NDIM(members) > 1 || ARR_ELEMTYPE(members) != BYTEAOID)
        ereport(ERROR, (errmsg("trajectory scope: members must be a 1-D bytea array")));
    if (ArrayGetNItems(ARR_NDIM(members), ARR_DIMS(members)) == 0) return;
    MemoryContext work = AllocSetContextCreate(CurrentMemoryContext,
        "containing observation roots", ALLOCSET_DEFAULT_SIZES);
    MemoryContext previous = MemoryContextSwitchTo(work);
    ArrayBuildState *roots = NULL;
    int count;
    hash128_t *ids = laplace_content_membership_entities(members, true, &count);
    for (int i = 0; i < count; ++i)
    {
        Datum id = hash128_to_datum(&ids[i]);
        scope_add_id(scope->roots, &roots, id, work);
        pfree(DatumGetPointer(id));
    }
    if (roots)
        laplace_content_trajectory_read(DatumGetArrayTypeP(makeArrayResult(roots, work)),
                                        scope_retain_trajectory, scope);
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
}

void
free_matcher(void *argument)
{
    MatcherCleanup *cleanup = argument;
    trajectory_suffix_matcher_free(cleanup->matcher);
}

int
record_successor(void *context, size_t ordinal, size_t stride,
                 const hash128_t *successor)
{
    SuccessorState *state = context;
    bool found;
    size_t matched_stride = stride;
    CHECK_FOR_INTERRUPTS();
    /* The matcher also calls back at packed-vertex boundaries with stride zero
     * and no successor so long scans stay cancellable; those calls and terminal
     * occurrences nominate nothing. This test must precede substituting the
     * whole-input stride. */
    if (stride == 0 || successor == NULL) return 0;
    if (state->input_stride) stride = state->input_stride;
    if (stride == 0 || stride < state->stride) return 0;
    if (state->scope && !state->trajectory && state->input_positions)
        state->trajectory = scope_keep_trajectory(
            state->physicality, state->entity, state->geometry, state->scope);
    if (state->scope && state->trajectory)
    {
        LaplaceTrajectoryScope *scope = state->scope;
        if (ordinal > SIZE_MAX - matched_stride)
            elog(ERROR, "trajectory scope: successor ordinal overflow");
        if (state->input_positions)
        {
            char key[sizeof(hash128_t) + sizeof(size_t)];
            size_t successor_ordinal = ordinal + matched_stride;
            memcpy(key, &state->trajectory->physicality, sizeof(hash128_t));
            memcpy(key + sizeof(hash128_t), &successor_ordinal, sizeof(size_t));
            hash_search(state->input_positions, key, HASH_ENTER, &found);
            if (found) return 0;
        }
        if (stride > state->stride) scope->position_count = 0;
        if (scope->position_count == scope->position_capacity)
        {
            size_t capacity = scope->position_capacity ? scope->position_capacity * 2 : 64;
            if (capacity > MaxAllocSize / sizeof(ScopedPosition))
                elog(ERROR, "trajectory scope: occurrence positions exceed allocation capacity");
            MemoryContext previous = MemoryContextSwitchTo(scope->owner);
            scope->positions = scope->positions
                ? repalloc(scope->positions, capacity * sizeof(ScopedPosition))
                : palloc(capacity * sizeof(ScopedPosition));
            scope->position_capacity = capacity;
            MemoryContextSwitchTo(previous);
        }
        scope->positions[scope->position_count++] = (ScopedPosition){
            .trajectory = state->trajectory, .ordinal = ordinal + matched_stride,
            .successor = *successor, .stride = (int)stride
        };
    }
    state->stride = stride;
    LaplaceContinuation *entry = hash_search(state->successors, successor, HASH_ENTER, &found);
    if (!found || entry->stride != (int) stride)
    {
        entry->occurrences = 0;
        entry->stride = (int) stride;
    }
    if (entry->occurrences == PG_INT64_MAX)
        ereport(ERROR, (errmsg("trajectory_continuations: occurrence count overflow")));
    ++entry->occurrences;
    return 0;
}

void
laplace_trajectory_scope_select(LaplaceTrajectoryScope *scope, Datum selected,
                                bool ordered)
{
    hash128_t id;
    bytea *value = DatumGetByteaPP(selected);
    if (VARSIZE_ANY_EXHDR(value) != sizeof(id))
        elog(ERROR, "trajectory scope: selection must be a 16-byte identity");
    memcpy(&id, VARDATA_ANY(value), sizeof(id));
    scope->advancing = ordered;
    size_t retained = 0;
    for (size_t i = 0; ordered && i < scope->position_count; ++i)
    {
        ScopedPosition position = scope->positions[i];
        if (!hash128_equals(&position.successor, &id)) continue;
        if (position.ordinal == SIZE_MAX)
            elog(ERROR, "trajectory scope: next ordinal overflow");
        ++position.ordinal;
        scope->positions[retained++] = position;
    }
    scope->position_count = retained;
}

static void
bind_input_trajectory(Datum physicality, Datum entity, Datum geometry, void *context)
{
    MembershipMatcher *match = context;
    match->state->trajectory = NULL;
    match->state->physicality = physicality;
    match->state->entity = entity;
    match->state->geometry = geometry;
    bytea *wkb = DatumGetByteaP(OidFunctionCall1(match->state->scope->as_binary, geometry));
    uint32 npoints;
    const unsigned char *points = laplace_trajectory_wkb_points(wkb, &npoints);
    if (trajectory_match_suffixes(match->matcher, points, npoints,
                                  record_successor, match->state) != 0)
        elog(ERROR, "trajectory input: invalid packed trajectory");
    pfree(wkb);
}

void
laplace_trajectory_scope_bind_input(LaplaceTrajectoryScope *scope,
                                    const LaplacePromptInput *input)
{
    MemoryContext work = AllocSetContextCreate(CurrentMemoryContext,
        "whole input occurrence binding", ALLOCSET_DEFAULT_SIZES);
    MemoryContext previous = MemoryContextSwitchTo(work);
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplaceContinuation);
    SuccessorState state = {.scope = scope,
        .input_stride = ArrayGetNItems(ARR_NDIM(input->context), ARR_DIMS(input->context)),
        .successors = hash_create("input successors", 128, &ctl, HASH_ELEM | HASH_BLOBS)};
    ctl.keysize = ctl.entrysize = sizeof(hash128_t) + sizeof(size_t);
    state.input_positions = hash_create("input occurrence identities", 128,
                                        &ctl, HASH_ELEM | HASH_BLOBS);
    uint8 max_tier = 0;
    const uint8_t *tiers = tier_tree_tier_array(input->tree);
    for (size_t i = 0; i < tier_tree_node_count(input->tree); ++i)
        if (tiers[i] > max_tier) max_tier = tiers[i];
    int min_tier = 0;
    size_t segment_count = 0;
    const uint32_t *segments = tier_tree_word_segments(input->tree, &segment_count);
    if (segments && segment_count > 0)
    {
        uint8 word_tier = 0;
        for (size_t i = 0; i < segment_count; ++i)
            if (segments[i] < tier_tree_node_count(input->tree) &&
                tiers[segments[i]] > word_tier)
                word_tier = tiers[segments[i]];
        min_tier = word_tier;
    }
    ArrayType *previous_cut = NULL;
    scope->position_count = 0;
    for (int tier = max_tier; tier >= min_tier; --tier)
    {
        ArrayType *cut = laplace_prompt_input_cut(input, (uint8) tier);
        if (previous_cut && VARSIZE(cut) == VARSIZE(previous_cut) &&
            memcmp(cut, previous_cut, VARSIZE(cut)) == 0)
        {
            pfree(cut);
            continue;
        }
        if (previous_cut) pfree(previous_cut);
        previous_cut = cut;
        Datum *values;
        bool *nulls;
        int n_ids;
        deconstruct_array(cut, BYTEAOID, -1, false, TYPALIGN_INT,
                          &values, &nulls, &n_ids);
        hash128_t *ids = palloc(sizeof(*ids) * Max(n_ids, 1));
        for (int i = 0; i < n_ids; ++i)
        {
            if (nulls[i]) elog(ERROR, "trajectory input: null canonical tree identity");
            ids[i] = datum_to_hash128(values[i]);
        }
        pfree(values);
        pfree(nulls);
        if (n_ids == 0) continue;
        MatcherCleanup *cleanup = palloc0(sizeof(*cleanup));
        cleanup->matcher = trajectory_suffix_matcher_create(ids, n_ids, n_ids);
        if (!cleanup->matcher)
            elog(ERROR, "trajectory input: allocating exact matcher failed");
        cleanup->callback.func = free_matcher;
        cleanup->callback.arg = cleanup;
        MemoryContextRegisterResetCallback(work, &cleanup->callback);
        MembershipMatcher match = {.matcher = cleanup->matcher, .state = &state};
        laplace_content_membership_read(cut, true, bind_input_trajectory, &match);
        trajectory_suffix_matcher_free(cleanup->matcher);
        cleanup->matcher = NULL;
        pfree(ids);
        CHECK_FOR_INTERRUPTS();
    }
    /* With no bound occurrence the advancing scope proposes no successor; it
     * is not re-matched on a shorter suffix or an isolated operand's context. */
    scope->advancing = true;
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
}
