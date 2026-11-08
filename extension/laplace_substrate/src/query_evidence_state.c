/*
 * query_evidence_state.c — Persistent LaplaceQueryState lifecycle, operand
 * role management, and incremental frontier extension.
 */
#include "query_evidence_internal.h"

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

void
laplace_qe_state_record_incident(LaplaceQueryState *state,
                                 const LaplaceConsensusRow *row,
                                 bool reverse)
{
    MemoryContext previous;
    int64 needed;
    int capacity;

    if (!state || state->incident_overflow || !row || row->object_is_null)
        return;
    needed = (int64) state->incident_count + 1;
    if (needed > INT_MAX || (uint64) needed > MaxAllocSize / sizeof(QueryIncidentRow))
    {
        state->incident_complete = false;
        state->incident_overflow = true;
        return;
    }
    previous = MemoryContextSwitchTo(state->owner);
    if (needed > state->incident_capacity)
    {
        capacity = state->incident_capacity ? state->incident_capacity : 64;
        while (capacity < needed)
        {
            int64 grown = (int64) capacity * 2;
            capacity = grown > INT_MAX ? (int) needed : (int) Min(grown, (int64) INT_MAX);
        }
        state->incident_rows = state->incident_rows
            ? (QueryIncidentRow *) repalloc(state->incident_rows,
                                            sizeof(QueryIncidentRow) * capacity)
            : (QueryIncidentRow *) palloc(sizeof(QueryIncidentRow) * capacity);
        state->incident_capacity = capacity;
    }
    state->incident_rows[state->incident_count].row = *row;
    state->incident_rows[state->incident_count].reverse = reverse;
    state->incident_count++;
    MemoryContextSwitchTo(previous);
}

static QueryCellKey
query_cell_key_of(const LaplaceQueryChannel *ch)
{
    QueryCellKey key;
    MemSet(&key, 0, sizeof(key));
    key.subject = ch->outbound ? ch->anchor : ch->candidate;
    key.type = ch->relation;
    key.object = ch->outbound ? ch->candidate : ch->anchor;
    key.native_claim = ch->native_claim ? 1 : 0;
    return key;
}

static void
query_cell_apply_bound(LaplaceQueryChannel *ch, const QueryBoundCell *cached)
{
    ch->witnesses = cached->witnesses;
    ch->confirm_occurrences = cached->confirm_occurrences;
    ch->draw_occurrences = cached->draw_occurrences;
    ch->refute_occurrences = cached->refute_occurrences;
    ch->observation_occurrences = cached->observation_occurrences;
    ch->observation_rows = cached->observation_rows;
    ch->distinct_sources = cached->distinct_sources;
    ch->distinct_contexts = cached->distinct_contexts;
    ch->provenance_root = cached->provenance_root;
    ch->calculation_confirm_occurrences = cached->calculation_confirm_occurrences;
    ch->calculation_draw_occurrences = cached->calculation_draw_occurrences;
    ch->calculation_refute_occurrences = cached->calculation_refute_occurrences;
    ch->calculation_occurrences = cached->calculation_occurrences;
    ch->calculation_rows = cached->calculation_rows;
    ch->distinct_calculation_sources = cached->distinct_calculation_sources;
    ch->distinct_calculation_contexts = cached->distinct_calculation_contexts;
    ch->calculation_provenance_root = cached->calculation_provenance_root;
}

static void
query_cell_store_bound(HTAB *bound_cells, const LaplaceQueryChannel *ch)
{
    QueryCellKey key = query_cell_key_of(ch);
    bool found;
    QueryBoundCell *cached = (QueryBoundCell *)
        hash_search(bound_cells, &key, HASH_ENTER, &found);
    cached->witnesses = ch->witnesses;
    cached->confirm_occurrences = ch->confirm_occurrences;
    cached->draw_occurrences = ch->draw_occurrences;
    cached->refute_occurrences = ch->refute_occurrences;
    cached->observation_occurrences = ch->observation_occurrences;
    cached->observation_rows = ch->observation_rows;
    cached->distinct_sources = ch->distinct_sources;
    cached->distinct_contexts = ch->distinct_contexts;
    cached->provenance_root = ch->provenance_root;
    cached->calculation_confirm_occurrences = ch->calculation_confirm_occurrences;
    cached->calculation_draw_occurrences = ch->calculation_draw_occurrences;
    cached->calculation_refute_occurrences = ch->calculation_refute_occurrences;
    cached->calculation_occurrences = ch->calculation_occurrences;
    cached->calculation_rows = ch->calculation_rows;
    cached->distinct_calculation_sources = ch->distinct_calculation_sources;
    cached->distinct_calculation_contexts = ch->distinct_calculation_contexts;
    cached->calculation_provenance_root = ch->calculation_provenance_root;
}

void
laplace_qe_state_bind_channels(const LaplaceQueryState *state,
                               ArrayType *operands,
                               LaplaceQueryChannel *channels,
                               int channel_count,
                               LaplaceQueryEvidenceStats *stats,
                               MemoryContext work)
{
    MemoryContext previous;
    int *missing_indices;
    LaplaceQueryChannel *unbound;
    int missing_count = 0;

    if (channel_count <= 0)
        return;
    if (!state || !state->bound_cells || stats != NULL)
    {
        laplace_qe_bind_channel_observations(operands, channels, channel_count, stats, work);
        if (state && state->bound_cells)
        {
            previous = MemoryContextSwitchTo(state->owner);
            for (int i = 0; i < channel_count; ++i)
                query_cell_store_bound(state->bound_cells, &channels[i]);
            MemoryContextSwitchTo(previous);
        }
        return;
    }

    missing_indices = (int *) palloc(sizeof(int) * channel_count);
    unbound = (LaplaceQueryChannel *) palloc(sizeof(LaplaceQueryChannel) * channel_count);
    for (int i = 0; i < channel_count; ++i)
    {
        QueryCellKey key = query_cell_key_of(&channels[i]);
        QueryBoundCell *cached = (QueryBoundCell *)
            hash_search(state->bound_cells, &key, HASH_FIND, NULL);
        if (cached != NULL)
        {
            query_cell_apply_bound(&channels[i], cached);
            continue;
        }
        missing_indices[missing_count] = i;
        unbound[missing_count] = channels[i];
        missing_count++;
    }
    if (missing_count > 0)
    {
        laplace_qe_bind_channel_observations(operands, unbound, missing_count, NULL, work);
        previous = MemoryContextSwitchTo(state->owner);
        for (int m = 0; m < missing_count; ++m)
        {
            channels[missing_indices[m]] = unbound[m];
            query_cell_store_bound(state->bound_cells, &unbound[m]);
        }
        MemoryContextSwitchTo(previous);
    }
    pfree(unbound);
    pfree(missing_indices);
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
    HASHCTL ctl;
    int count = 0;

    laplace_qe_validate_id_array(operands, "operands");
    laplace_qe_validate_id_array(types, "relation types");
    if (!operands)
        ereport(ERROR, (errmsg("query evidence: ordered operands are required")));
    if (fanout < 0)
        ereport(ERROR, (errmsg("query evidence: fanout must not be negative")));

    owner = AllocSetContextCreate(parent, "query evidence state", ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(owner);
    state = (LaplaceQueryState *) palloc0(sizeof(*state));
    state->owner = owner;
    state->fanout = fanout;
    state->incident_complete = fanout > 0;
    MemSet(&ctl, 0, sizeof(ctl));
    ctl.keysize = sizeof(QueryCellKey);
    ctl.entrysize = sizeof(QueryBoundCell);
    ctl.hcxt = owner;
    state->bound_cells = hash_create("query state bound cells", 128,
                                     &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
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
    initial = laplace_qe_channels_internal(operands, state->types, fanout, &count, stats, state);
    state->expanded_operand_count = state->operand_count;
    query_state_reserve(state, count);
    if (count > 0)
    {
        memcpy(state->channels, initial, sizeof(*initial) * count);
        state->channel_count = count;
        if (stats != NULL)
            state->bound_channel_count = count;
        for (int i = 0; i < count; ++i)
            state->channels[i].operand_role =
                laplace_qe_state_operand_role(state, state->channels[i].ordinal);
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
        if (!laplace_qe_operand_role_valid(roles[i]))
            ereport(ERROR, (errmsg("query evidence: invalid operand role %u", roles[i])));

    previous = MemoryContextSwitchTo(state->owner);
    if (role_count > 0)
        memcpy(state->operand_roles, roles, sizeof(uint32) * role_count);
    for (int i = 0; i < state->channel_count; ++i)
        state->channels[i].operand_role =
            laplace_qe_state_operand_role(state, state->channels[i].ordinal);
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
laplace_qe_state_ensure_expanded(const LaplaceQueryState *state)
{
    LaplaceQueryState *mutable_state;
    MemoryContext previous;
    Datum *values = NULL;
    bool *nulls = NULL;
    int total = 0;
    int start, pending_count, count = 0;
    ArrayType *pending;
    LaplaceQueryChannel *added;

    if (!state || state->expanded_operand_count >= state->operand_count)
        return;
    mutable_state = (LaplaceQueryState *) state;
    previous = MemoryContextSwitchTo(mutable_state->owner);
    deconstruct_array(mutable_state->operands, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &total);
    start = mutable_state->expanded_operand_count;
    pending_count = total - start;
    if (pending_count > 0)
    {
        pending = construct_array(values + start, pending_count,
                                  BYTEAOID, -1, false, TYPALIGN_INT);
        added = laplace_qe_channels_internal(pending, mutable_state->types,
                                             mutable_state->fanout, &count,
                                             NULL, mutable_state);
        query_state_reserve(mutable_state, count);
        for (int i = 0; i < count; ++i)
        {
            added[i].ordinal += start;
            added[i].operand_role =
                laplace_qe_state_operand_role(mutable_state, added[i].ordinal);
            mutable_state->channels[mutable_state->channel_count++] = added[i];
        }
        if (added)
            pfree(added);
        pfree(pending);
    }
    if (total > 0)
    {
        pfree(values);
        pfree(nulls);
    }
    mutable_state->expanded_operand_count = mutable_state->operand_count;
    MemoryContextSwitchTo(previous);
}

void
laplace_query_state_bind_channels(const LaplaceQueryState *state,
                                  LaplaceQueryChannel *channels,
                                  int channel_count)
{
    MemoryContext work;
    MemoryContext previous;

    if (!state || !channels || channel_count <= 0)
        return;
    work = AllocSetContextCreate(CurrentMemoryContext,
                                 "query state channel subset bind",
                                 ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(work);
    laplace_qe_state_bind_channels(state, state->operands, channels,
                                   channel_count, NULL, work);
    MemoryContextSwitchTo(previous);
    MemoryContextDelete(work);
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
    int prior_count;

    if (!state)
        return;
    if (!laplace_qe_operand_role_valid(operand_role))
        ereport(ERROR, (errmsg("query evidence: invalid appended operand role %u", operand_role)));
    if (stats) MemSet(stats, 0, sizeof(*stats));
    laplace_qe_validate_id_array(selected, "selected frontier");
    if (!selected) return;
    selected_count = ArrayGetNItems(ARR_NDIM(selected), ARR_DIMS(selected));
    if (selected_count == 0) return;
    if (selected_count > INT_MAX - state->operand_count)
        ereport(ERROR, (errmsg("query evidence: working-state ordinal exceeds int capacity")));

    previous = MemoryContextSwitchTo(state->owner);
    if (stats != NULL)
        laplace_qe_state_ensure_expanded(state);
    prior_count = state->operand_count;
    query_state_append_operands(state, selected, selected_count, operand_role);
    state->operand_count += selected_count;
    if (stats != NULL)
    {
        added = laplace_qe_channels_internal(selected, state->types,
                                             state->fanout, &count, stats, state);
        query_state_reserve(state, count);
        for (int i = 0; i < count; ++i)
        {
            added[i].ordinal += prior_count;
            added[i].operand_role = operand_role;
            state->channels[state->channel_count++] = added[i];
        }
        if (added)
            pfree(added);
        state->expanded_operand_count = state->operand_count;
        state->bound_channel_count = state->channel_count;
    }
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

int
laplace_query_state_channel_count(const LaplaceQueryState *state)
{
    laplace_qe_state_ensure_expanded(state);
    return state ? state->channel_count : 0;
}

const LaplaceQueryChannel *
laplace_query_state_channels_unbound(const LaplaceQueryState *state, int *count)
{
    if (!count)
        ereport(ERROR, (errmsg("query evidence: channel count output is required")));
    laplace_qe_state_ensure_expanded(state);
    *count = state ? state->channel_count : 0;
    return state ? state->channels : NULL;
}

const LaplaceQueryChannel *
laplace_query_state_channels(const LaplaceQueryState *state, int *count)
{
    if (!count)
        ereport(ERROR, (errmsg("query evidence: channel count output is required")));
    laplace_qe_state_ensure_expanded(state);
    if (state && state->bound_channel_count < state->channel_count)
    {
        LaplaceQueryState *mutable_state = (LaplaceQueryState *) state;
        MemoryContext work = AllocSetContextCreate(CurrentMemoryContext,
            "query state lazy bind", ALLOCSET_DEFAULT_SIZES);
        MemoryContext previous = MemoryContextSwitchTo(work);
        laplace_qe_state_bind_channels(
            mutable_state,
            mutable_state->operands,
            mutable_state->channels + mutable_state->bound_channel_count,
            mutable_state->channel_count - mutable_state->bound_channel_count,
            NULL,
            work);
        mutable_state->bound_channel_count = mutable_state->channel_count;
        MemoryContextSwitchTo(previous);
        MemoryContextDelete(work);
    }
    *count = state ? state->channel_count : 0;
    return state ? state->channels : NULL;
}

static ArrayType *
query_state_distinct_ids(const LaplaceQueryState *state, bool relations)
{
    MemoryContext owner = CurrentMemoryContext;
    HASHCTL ctl;
    HTAB *seen;
    ArrayBuildState *values = NULL;

    laplace_qe_state_ensure_expanded(state);
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
