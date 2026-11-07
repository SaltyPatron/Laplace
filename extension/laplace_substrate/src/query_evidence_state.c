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
