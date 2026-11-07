#ifndef LAPLACE_PROMPT_INTENT_CONTRACT_H
#define LAPLACE_PROMPT_INTENT_CONTRACT_H

static inline bool
laplace_prompt_result_channel(const LaplaceQueryChannel *channel)
{
    const laplace_relation_def_t *def = NULL;
    if (channel->outbound) return true;
    return laplace_relation_lookup(&channel->relation, &def) == 0 &&
           def != NULL && def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC;
}

typedef struct LaplacePromptContractCell
{
    LaplaceObservationCell cell;
    bool positive;
} LaplacePromptContractCell;

typedef struct LaplacePromptContractRead
{
    LaplacePromptIntent *intent;
    const hash128_t *active_context;
    HTAB *witnesses;
    HTAB *cells;
    int fanout;
} LaplacePromptContractRead;

static void
laplace_prompt_contract_observation(int ordinal, int16 role,
                                    const LaplaceObservation *row, void *context)
{
    LaplacePromptContractRead *read = context;
    LaplaceObservation *stored;
    MemoryContext previous;
    bool found;
    (void) ordinal;
    if (role != 1 || row->object_null || row->source_null || row->context_null ||
        row->occurrences <= 0 || !hash128_eq(&row->subject, &read->intent->root) ||
        !hash128_eq(&row->context, read->active_context))
        return;
    previous = MemoryContextSwitchTo(read->intent->owner);
    if (!hash_search(read->witnesses, &row->id, HASH_FIND, NULL) &&
        hash_get_num_entries(read->witnesses) >= read->fanout)
    {
        /* Continue the complete set read, but never compile the retained
         * prefix as a smaller contract when its declared envelope is exceeded. */
        read->intent->budget_exhausted = true;
        MemoryContextSwitchTo(previous);
        return;
    }
    stored = hash_search(read->witnesses, &row->id, HASH_ENTER, NULL);
    *stored = *row;
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    LaplacePromptContractCell *cell = hash_search(read->cells, &key, HASH_ENTER, &found);
    if (!found) cell->positive = false;
    MemoryContextSwitchTo(previous);
}

static void
laplace_prompt_contract_standing(const LaplaceConsensusRow *row, void *context)
{
    LaplacePromptContractRead *read = context;
    LaplaceObservationCell key;
    LaplacePromptContractCell *cell;
    if (row->object_is_null) return;
    key = (LaplaceObservationCell) {row->subject, row->type, row->object};
    cell = hash_search(read->cells, &key, HASH_FIND, NULL);
    if (cell)
        cell->positive = laplace_walk_edge_weight(row->rating, row->rd) > 0.0;
}

static inline bool
laplace_prompt_contract_positive(const LaplacePromptContractRead *read,
                                 const LaplaceObservation *row)
{
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    LaplacePromptContractCell *cell = hash_search(read->cells, &key, HASH_FIND, NULL);
    return cell && cell->positive;
}

static inline bool
laplace_prompt_same_scope(const LaplaceObservation *a, const LaplaceObservation *b)
{
    return hash128_eq(&a->source, &b->source) &&
           hash128_eq(&a->context, &b->context);
}

static inline bool
laplace_prompt_contract_refuted(const LaplaceObservation *row,
                                const LaplaceObservation *rows, int count)
{
    for (int i = 0; i < count; ++i)
        if (rows[i].outcome == LAPLACE_ATTESTATION_OUTCOME_REFUTE &&
            laplace_prompt_same_scope(row, &rows[i]) &&
            hash128_eq(&row->type, &rows[i].type) &&
            hash128_eq(&row->object, &rows[i].object))
            return true;
    return false;
}

static int
laplace_prompt_witness_compare(const void *left, const void *right)
{
    const LaplaceObservation *a = left, *b = right;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

/* Compile invocations in the caller's active context. One source witnesses
 * R CALLS O and R HAS_INPUT I in that context, where R is the whole
 * observation and I is bound to current occurrences; O becomes the result
 * relation, anchored exactly at I.
 *
 * All typed declarations are read before standing is checked: a declared
 * input that is refuted or lacks positive standing invalidates the whole call.
 * One set observation read, then one set consensus probe. Exceeding fanout
 * marks the intent budget-exhausted instead of compiling a retained prefix.
 * Only witnessed declarations create an invocation. */
static inline void
laplace_prompt_intent_compile(LaplacePromptIntent *intent,
                              const hash128_t *active_context, int fanout)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    HASHCTL ctl = {0};
    HASH_SEQ_STATUS sequence;
    LaplaceObservation *entry, *rows;
    int row_count = 0;
    Datum root;
    ArrayType *roots, *types, *objects;
    ArrayBuildState *type_ids = NULL, *object_ids = NULL;
    LaplacePromptContractRead read = {
        .intent = intent, .active_context = active_context, .fanout = fanout};

    intent->explicit_invocation = true;
    intent->active_context = *active_context;
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(LaplaceObservation);
    ctl.hcxt = intent->owner;
    read.witnesses = hash_create("prompt invocation witnesses", Max(Min(fanout, 128), 1), &ctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ctl.keysize = sizeof(LaplaceObservationCell);
    ctl.entrysize = sizeof(LaplacePromptContractCell);
    read.cells = hash_create("prompt invocation cells", Max(Min(fanout, 128), 1), &ctl,
                             HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    for (size_t i = 0; i < laplace_relation_table_count; ++i)
    {
        hash128_t relation_id;
        /* The generated descriptor table stores metadata; canonical IDs live
         * in the native law cache and must be obtained through its accessor. */
        if (laplace_relation_type_id(laplace_relation_table[i].canonical,
                                     &relation_id) != 0)
            elog(ERROR, "prompt contract: governed relation identity is unavailable");
        if (laplace_prompt_contract_relation(&relation_id))
        {
            /* A partial protocol read could silently omit a required input.
             * Decline the invocation before any witness read if its complete
             * declaration families fall outside the hard caller scope. */
            if (!laplace_prompt_relation_allowed(intent, &relation_id))
            {
                hash_destroy(read.witnesses);
                hash_destroy(read.cells);
                MemoryContextSwitchTo(previous);
                return;
            }
            Datum id = hash128_to_datum(&relation_id);
            type_ids = accumArrayResult(type_ids, id, false, BYTEAOID, intent->owner);
            pfree(DatumGetPointer(id));
        }
    }
    types = type_ids ? DatumGetArrayTypeP(makeArrayResult(type_ids, intent->owner)) :
        construct_empty_array(BYTEAOID);
    root = hash128_to_datum(&intent->root);
    roots = construct_array(&root, 1, BYTEAOID, -1, false, TYPALIGN_INT);
    laplace_observation_read(roots, NULL, types, 1,
                             laplace_prompt_contract_observation, &read);
    if (intent->budget_exhausted)
    {
        hash_destroy(read.witnesses);
        hash_destroy(read.cells);
        pfree(roots); pfree(types); pfree(DatumGetPointer(root));
        MemoryContextSwitchTo(previous);
        return;
    }
    if (hash_get_num_entries(read.witnesses) > INT_MAX ||
        hash_get_num_entries(read.witnesses) > MaxAllocSize / sizeof(*rows))
        elog(ERROR, "prompt contract: witness set exceeds allocation capacity");
    row_count = (int) hash_get_num_entries(read.witnesses);
    rows = palloc(sizeof(*rows) * Max(row_count, 1));
    hash_seq_init(&sequence, read.witnesses);
    for (int i = 0; (entry = hash_seq_search(&sequence)) != NULL; ++i)
    {
        Datum id = hash128_to_datum(&entry->object);
        rows[i] = *entry;
        object_ids = accumArrayResult(object_ids, id, false, BYTEAOID, intent->owner);
        pfree(DatumGetPointer(id));
    }
    hash_destroy(read.witnesses);
    objects = object_ids ? DatumGetArrayTypeP(makeArrayResult(object_ids, intent->owner)) :
        construct_empty_array(BYTEAOID);
    laplace_consensus_scan(roots, objects, types, laplace_prompt_contract_standing, &read, NULL);
    pfree(roots); pfree(types); pfree(objects); pfree(DatumGetPointer(root));
    qsort(rows, row_count, sizeof(*rows), laplace_prompt_witness_compare);

    for (int i = 0; i < row_count; ++i)
    {
        const LaplaceObservation *call = &rows[i];
        LaplacePromptRelationRead operation = {0};
        bool valid = true;
        if (laplace_prompt_contract_relation(&call->type) != 1 ||
            call->outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM ||
            !laplace_prompt_contract_positive(&read, call) ||
            laplace_prompt_contract_refuted(call, rows, row_count))
            continue;
        operation.result_relation = call->object;
        operation.source = call->source;
        operation.context = call->context;
        operation.call_witness = call->id;
        operation.inputs = palloc0(sizeof(*operation.inputs) * Max(row_count, 1));
        for (int j = 0; j < row_count; ++j)
        {
            const LaplaceObservation *input = &rows[j];
            LaplacePromptIntentBinding *binding;
            LaplacePromptOperand *operand;
            if (!laplace_prompt_same_scope(call, input) ||
                laplace_prompt_contract_relation(&input->type) != 2)
                continue;
            binding = hash_search(intent->bindings, &input->object, HASH_FIND, NULL);
            if (!binding || input->outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM ||
                !laplace_prompt_contract_positive(&read, input) ||
                laplace_prompt_contract_refuted(input, rows, row_count))
            {
                valid = false;
                break;
            }
            operand = &operation.inputs[operation.input_count++];
            operand->id = input->object;
            operand->witness = input->id;
            operand->origins = bms_copy(binding->origins);
            operation.operand_origins = bms_add_members(operation.operand_origins,
                                                        binding->origins);
        }
        if (!valid || operation.input_count == 0)
        {
            for (int j = 0; j < operation.input_count; ++j)
                bms_free(operation.inputs[j].origins);
            pfree(operation.inputs);
            bms_free(operation.operand_origins);
            continue;
        }
        if (intent->relation_count == intent->relation_capacity)
        {
            int64 capacity = intent->relation_capacity ?
                (int64) intent->relation_capacity * 2 : 8;
            if (capacity > INT_MAX ||
                (Size) capacity > MaxAllocSize / sizeof(LaplacePromptRelationRead))
                elog(ERROR, "prompt contract: operation set exceeds allocation capacity");
            intent->operations = intent->operations ?
                repalloc(intent->operations, sizeof(LaplacePromptRelationRead) * capacity) :
                palloc(sizeof(LaplacePromptRelationRead) * capacity);
            intent->relation_capacity = (int) capacity;
        }
        intent->operations[intent->relation_count++] = operation;
    }
    for (int i = 0; i < intent->relation_count; ++i)
        for (int j = i + 1; j < intent->relation_count; ++j)
            if (!hash128_eq(&intent->operations[i].result_relation, &intent->operations[j].result_relation) ||
                !laplace_prompt_same_inputs(&intent->operations[i], &intent->operations[j]))
                intent->ambiguous = true;
    hash_destroy(read.cells);
    pfree(rows);
    MemoryContextSwitchTo(previous);
}

/* Advance exactly ONE witnessed naming hop. Stage additions before merging so
 * iteration order cannot secretly turn one coupling round into many hops.
 * Return changed identities for one set-sized frontier extension. All input
 * channels belong to the unmasked active field, and all alternatives survive. */
static inline ArrayType *
laplace_prompt_intent_couple(LaplacePromptIntent *intent,
                             const LaplaceQueryChannel *channels, int count)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    HTAB *pending = laplace_prompt_binding_table("prompt pending bindings", intent->owner);
    HASH_SEQ_STATUS sequence;
    LaplacePromptIntentBinding *entry;
    ArrayBuildState *changed = NULL;
    /* A witnessed complete parse binds the lemma at an exact token occurrence.
     * Its head/features/roles remain in the separate structural alternative;
     * neither lemma projection nor parse mood establishes a speech act. */
    if (intent->structure && !intent->structure->budget_exhausted)
        for (int p = 0; p < intent->structure->count; ++p)
        {
            const LaplacePromptParse *parse = intent->structure->parses[p];
            if (!parse->supported) continue;
            for (size_t t = 0; t < parse->decoded.token_count; ++t)
            {
                bool found;
                const hash128_t *lemma = &parse->decoded.tokens[t].lemma_id;
                int origin = parse->token_origins[t];
                if (origin < 0) continue;
                entry = hash_search(pending, lemma, HASH_ENTER, &found);
                if (!found) entry->origins = NULL;
                entry->origins = bms_add_member(entry->origins, origin);
            }
        }
    for (int i = 0; i < count; ++i)
    {
        const LaplaceQueryChannel *channel = &channels[i];
        const Bitmapset *anchor;
        bool found;
        if (!laplace_prompt_binding_channel(channel)) continue;
        anchor = laplace_prompt_intent_origins(intent, &channel->anchor);
        if (!anchor) continue;
        entry = hash_search(pending, &channel->candidate, HASH_ENTER, &found);
        if (!found) entry->origins = NULL;
        entry->origins = bms_add_members(entry->origins, anchor);
    }
    hash_seq_init(&sequence, pending);
    while ((entry = hash_seq_search(&sequence)) != NULL)
    {
        bool found;
        LaplacePromptIntentBinding *target =
            hash_search(intent->bindings, &entry->id, HASH_ENTER, &found);
        if (!found) target->origins = NULL;
        if (!bms_is_subset(entry->origins, target->origins))
        {
            Datum id = hash128_to_datum(&entry->id);
            target->origins = bms_add_members(target->origins, entry->origins);
            changed = accumArrayResult(changed, id, false, BYTEAOID, intent->owner);
            pfree(DatumGetPointer(id));
        }
        bms_free(entry->origins);
    }
    hash_destroy(pending);
    ArrayType *result = changed ? DatumGetArrayTypeP(makeArrayResult(changed, intent->owner)) :
        construct_empty_array(BYTEAOID);
    MemoryContextSwitchTo(previous);
    return result;
}

#endif
