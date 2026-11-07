#include "task_shape_internal.h"

static bool
shape_contract(const ShapeRead *read, const ShapeStructure *shape,
               const LaplaceObservation *example, const LaplaceObservation *rows, int count,
               const LaplaceObservation **call, const LaplaceObservation **slots)
{
    *call = NULL;
    for (int i = 0; i < count; ++i)
    {
        const LaplaceObservation *row = rows + i;
        if (!hash128_eq(&row->subject, &shape->id) || !laplace_prompt_same_scope(example, row))
            continue;
        if (hash128_eq(&row->type, &read->call_type))
        {
            if (!hash128_eq(&row->object, &shape->shape.predicate) ||
                !shape_positive(read, row, rows, count)) return false;
            if (!*call) *call = row;
        }
        if (hash128_eq(&row->type, &read->input_type))
        {
            size_t slot;
            for (slot = 0; slot < shape->shape.slot_count; ++slot)
                if (hash128_eq(&row->object, shape->shape.slots + slot * shape->shape.slot_stride)) break;
            if (slot == shape->shape.slot_count || !shape_positive(read, row, rows, count))
                return false;
            if (!slots[slot]) slots[slot] = row;
        }
    }
    if (!*call) return false;
    for (size_t i = 0; i < shape->shape.slot_count; ++i)
        if (!slots[i]) return false;
    return true;
}

static int
shape_identity_compare(const void *a, const void *b)
{
    return memcmp(a, b, sizeof(hash128_t));
}

static bool
shape_same_bindings(const LaplacePromptRelationRead *a, const LaplacePromptRelationRead *b)
{
    if (!(a->shape_id.hi || a->shape_id.lo || b->shape_id.hi || b->shape_id.lo))
        return laplace_prompt_same_inputs(a, b);
    if (a->input_count != b->input_count) return false;
    for (int i = 0; i < a->input_count; ++i)
        if (!hash128_eq(&a->inputs[i].id, &b->inputs[i].id) ||
            !bms_equal(a->inputs[i].origins, b->inputs[i].origins)) return false;
    return true;
}

static void
shape_recipe_count(StringInfo bytes, uint32 count)
{
    unsigned char encoded[4] = {(unsigned char) (count >> 24), (unsigned char) (count >> 16),
                                (unsigned char) (count >> 8), (unsigned char) count};
    appendBinaryStringInfo(bytes, (const char *) encoded, 4);
}

/* With no supported parse of the observation, the operation's current parse is
 * a calculated recipe id over shape, exemplar, observation forms with their
 * origins, and the chosen inputs. No HAS_PARSE observation is written. */
static hash128_t
shape_projection_id(const ShapeRead *read, const ShapeStructure *shape,
                     const SlotChoices *choices, const int *selected)
{
    StringInfoData bytes;
    hash128_t domain, result;
    hash128_blake3_str("laplace/task-shape/request-projection/v1", &domain);
    initStringInfo(&bytes);
    appendBinaryStringInfo(&bytes, (const char *) &domain, sizeof(domain));
    appendBinaryStringInfo(&bytes, (const char *) &shape->id, sizeof(hash128_t));
    appendBinaryStringInfo(&bytes, (const char *) &shape->shape.exemplar_parse, sizeof(hash128_t));
    appendBinaryStringInfo(&bytes, (const char *) &read->intent->root, sizeof(hash128_t));
    shape_recipe_count(&bytes, read->intent->structure->form_count);
    for (int i = 0; i < read->intent->structure->form_count; ++i)
    {
        appendBinaryStringInfo(&bytes, (const char *) &read->intent->structure->forms[i], sizeof(hash128_t));
        shape_recipe_count(&bytes, read->intent->structure->origins[i]);
    }
    shape_recipe_count(&bytes, shape->shape.slot_count);
    for (size_t i = 0; i < shape->shape.slot_count; ++i)
        appendBinaryStringInfo(&bytes, (const char *) &choices[i].ids[selected[i]], sizeof(hash128_t));
    hash128_blake3((const uint8_t *) bytes.data, bytes.len, &result);
    pfree(bytes.data);
    return result;
}

static void
shape_append_operation(ShapeRead *read, const ShapeStructure *shape,
                       const LaplacePromptParse *current, const LaplaceObservation *example,
                       const LaplaceObservation *call, const LaplaceObservation *parse_witness,
                       const LaplaceObservation *exemplar_witness,
                       const LaplaceObservation **slot_witnesses, const SlotChoices *choices,
                       const int *selected)
{
    LaplacePromptIntent *intent = read->intent;
    if (intent->relation_count == intent->relation_capacity)
    {
        int64 capacity = intent->relation_capacity ? (int64) intent->relation_capacity * 2 : 8;
        if (capacity > INT_MAX || (Size) capacity > MaxAllocSize / sizeof(*intent->operations))
            elog(ERROR, "task shape: operation set exceeds allocation capacity");
        intent->operations = intent->operations ?
            repalloc(intent->operations, capacity * sizeof(*intent->operations)) :
            palloc(capacity * sizeof(*intent->operations));
        intent->relation_capacity = capacity;
    }
    LaplacePromptRelationRead *operation = intent->operations + intent->relation_count++;
    MemSet(operation, 0, sizeof(*operation));
    operation->result_relation = shape->shape.predicate;
    operation->source = example->source;
    operation->context = example->context;
    operation->call_witness = call->id;
    operation->shape_id = shape->id;
    operation->exemplar_parse = shape->shape.exemplar_parse;
    operation->current_parse = current ? current->id : shape_projection_id(read, shape, choices, selected);
    operation->applicability_witness = example->id;
    if (parse_witness) operation->parse_witness = parse_witness->id;
    operation->exemplar_parse_witness = exemplar_witness->id;
    operation->input_count = shape->shape.slot_count;
    operation->inputs = palloc0(operation->input_count * sizeof(*operation->inputs));
    for (int i = 0; i < operation->input_count; ++i)
    {
        operation->inputs[i].id = choices[i].ids[selected[i]];
        operation->inputs[i].witness = slot_witnesses[i]->id;
        operation->inputs[i].origins = bms_make_singleton(choices[i].origin);
        operation->operand_origins = bms_add_member(operation->operand_origins, choices[i].origin);
    }
}

static void
shape_instantiate(ShapeRead *read, const ShapeStructure *shape, const ShapeStructure *exemplar,
                   const LaplacePromptParse *current, const LaplaceObservation *example,
                   const LaplaceObservation *call, const LaplaceObservation **slot_witnesses,
                   const LaplaceObservation *exemplar_witness)
{
    size_t slots = shape->shape.slot_count;
    if (slots > INT_MAX || slots > MaxAllocSize / sizeof(SlotChoices))
        elog(ERROR, "task shape: input slot set exceeds allocation capacity");
    size_t *ordinals = palloc(slots * sizeof(*ordinals));
    int match = current ?
        laplace_task_shape_match_view(&shape->shape, &exemplar->parse, &current->decoded, ordinals) :
        laplace_task_shape_match_forms_view(&shape->shape, &exemplar->parse,
            read->intent->structure->forms, read->intent->structure->form_count, ordinals);
    if (match < 0) elog(ERROR, "task shape: exact structural matching failed");
    if (!match) { pfree(ordinals); return; }
    const LaplaceObservation *parse_witness = current ? shape_current_witness(current) : NULL;
    if (current && !parse_witness) { pfree(ordinals); return; }
    SlotChoices *choices = palloc0(slots * sizeof(*choices));
    int *selected = palloc0(slots * sizeof(*selected));
    uint64 combinations = 1;
    laplace_task_shape_markers_extended_t markers;
    laplace_task_shape_markers_extended_init(&markers);
    for (size_t slot = 0; slot < slots; ++slot)
    {
        const hash128_t *fields = shape->shape.slots + slot * shape->shape.slot_stride;
        HASH_SEQ_STATUS scan;
        LaplacePromptIntentBinding *binding;
        int count = 0;
        choices[slot].origin = current ? current->token_origins[ordinals[slot]] :
            read->intent->structure->origins[ordinals[slot]];
        if (choices[slot].origin < 0) { combinations = 0; break; }
        if ((Size) read->fanout > MaxAllocSize / sizeof(hash128_t))
            elog(ERROR, "task shape: input candidate envelope exceeds allocation capacity");
        choices[slot].ids = palloc((Size) Max(read->fanout, 1) * sizeof(hash128_t));
        if (shape->shape.slot_stride == 4 && hash128_eq(fields + 3, &markers.current_form))
        {
            /* A current-form slot takes the observed form at the matched
             * occurrence itself, if it carries the slot type; bindings to that
             * occurrence are not candidates. */
            const hash128_t *form = read->intent->structure->forms + ordinals[slot];
            ShapeEntityType key = {*form, fields[2]};
            if (hash_search(read->entity_types, &key, HASH_FIND, NULL))
                choices[slot].ids[count++] = *form;
        }
        else
        {
            hash_seq_init(&scan, read->intent->bindings);
            while ((binding = hash_seq_search(&scan)) != NULL)
            {
                if (!bms_is_member(choices[slot].origin, binding->origins)) continue;
                ShapeEntityType key = {binding->id, fields[2]};
                if (!hash_search(read->entity_types, &key, HASH_FIND, NULL)) continue;
                if (count >= read->fanout) { read->failed = true; continue; }
                choices[slot].ids[count++] = binding->id;
            }
        }
        choices[slot].count = count;
        qsort(choices[slot].ids, count, sizeof(hash128_t), shape_identity_compare);
        if (!count) { combinations = 0; break; }
        if (combinations > (uint64) read->fanout / count)
        { read->failed = true; break; }
        combinations *= count;
    }
    if ((uint64) read->intent->relation_count + combinations > (uint64) read->fanout)
        read->failed = true;
    for (uint64 i = 0; !read->failed && i < combinations; ++i)
    {
        shape_append_operation(read, shape, current, example, call, parse_witness,
                               exemplar_witness, slot_witnesses, choices, selected);
        for (size_t slot = slots; slot-- > 0;)
        {
            if (++selected[slot] < choices[slot].count) break;
            selected[slot] = 0;
        }
        CHECK_FOR_INTERRUPTS();
    }
    for (size_t i = 0; i < slots; ++i)
        if (choices[i].ids) pfree(choices[i].ids);
    pfree(selected); pfree(choices); pfree(ordinals);
}

void
laplace_task_shape_compile(LaplacePromptIntent *intent, int fanout)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    ShapeRead read = {.intent = intent, .owner = intent->owner, .fanout = fanout};
    LaplacePromptStructure *structure = intent->structure;
    ArrayBuildState *forms = NULL;
    ArrayType *members, *parse_ids, *shape_ids, *types;
    HASH_SEQ_STATUS scan;
    LaplaceObservation *row, *rows;
    int count;
    if (!structure || structure->form_count == 0 || structure->budget_exhausted || fanout <= 0)
    {
        intent->budget_exhausted |= (structure && structure->budget_exhausted) || fanout <= 0;
        MemoryContextSwitchTo(previous);
        return;
    }
    for (int i = 0; i < structure->form_count; ++i)
    {
        Datum id = hash128_to_datum(&structure->forms[i]);
        forms = accumArrayResult(forms, id, false, BYTEAOID, read.owner);
        pfree(DatumGetPointer(id));
    }
    if (!forms) { MemoryContextSwitchTo(previous); return; }
    read.structures = shape_table("task shape structures", sizeof(hash128_t), sizeof(ShapeStructure), read.owner);
    read.witnesses = shape_table("task shape observations", sizeof(hash128_t), sizeof(LaplaceObservation), read.owner);
    read.cells = shape_table("task shape standing", sizeof(LaplaceObservationCell), sizeof(ShapeCell), read.owner);
    read.entity_types = shape_table("task shape entity types", sizeof(ShapeEntityType), sizeof(ShapeEntityType), read.owner);
    /* The shape protocol relations must resolve to exactly these canonical
     * registry entries; an alias resolving elsewhere is an error. A protocol
     * relation outside the caller's hard relation-type constraint ends
     * compilation with no operations. */
    static const char *const protocol_relations[] = {
        "IS_EXAMPLE_OF", "CALLS", "HAS_INPUT", "HAS_PARSE"
    };
    hash128_t *protocol_ids[] = {
        &read.example_type, &read.call_type, &read.input_type, &read.parse_type
    };
    for (size_t i = 0; i < lengthof(protocol_relations); ++i)
    {
        const laplace_relation_def_t *definition = NULL;
        if (laplace_relation_resolve(protocol_relations[i], protocol_ids[i]) != 0 ||
            laplace_relation_lookup(protocol_ids[i], &definition) != 0 || !definition ||
            strcmp(definition->canonical, protocol_relations[i]) != 0)
            elog(ERROR, "task shape: declared relation is absent from canonical registry");
        if (!laplace_prompt_relation_allowed(intent, protocol_ids[i])) goto done;
    }
    Oid physicalities = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
    Oid geometry = get_atttype(physicalities, get_attnum(physicalities, "trajectory"));
    if (!OidIsValid(geometry)) elog(ERROR, "task shape: geometry type is unavailable");
    read.as_binary = LookupFuncName(list_make2(makeString("public"), makeString("st_asbinary")), 1, &geometry, false);
    members = DatumGetArrayTypeP(makeArrayResult(forms, read.owner));
    laplace_ud_markers_t parse_markers;
    laplace_ud_markers_init(&parse_markers);
    ArrayType *required = hash128_array_from_ids(&parse_markers.schema_v1, 1);
    /* Trajectories containing an observation form AND the parse schema
     * constituent; exceeding fanout marks the compile budget exhausted. */
    read.failed = !laplace_typed_membership_read_with_required(members, false,
        required, 8, fanout, shape_receive_structure, &read);
    pfree(required);
    /* Vertex-flagged parses carry no schema constituent. This second read only
     * adds those; other structures sharing a form are dropped, so its fanout
     * bound does not mark the budget exhausted. */
    read.vertex_only = true;
    (void) laplace_typed_membership_read_with_required(members, false,
        NULL, 8, fanout, shape_receive_structure, &read);
    read.vertex_only = false;
    pfree(members);
    if (read.failed) goto done;
    parse_ids = shape_structure_ids(&read, true);
    types = hash128_array_from_ids(&read.example_type, 1);
    laplace_observation_read(parse_ids, NULL, types, 1, shape_receive_witness, &read);
    pfree(types);
    if (read.failed) { pfree(parse_ids); goto done; }
    ArrayBuildState *nominated = NULL;
    hash_seq_init(&scan, read.witnesses);
    while ((row = hash_seq_search(&scan)) != NULL)
    {
        Datum id = hash128_to_datum(&row->object);
        nominated = accumArrayResult(nominated, id, false, BYTEAOID, read.owner);
        pfree(DatumGetPointer(id));
    }
    if (!nominated) { pfree(parse_ids); goto done; }
    shape_ids = DatumGetArrayTypeP(makeArrayResult(nominated, read.owner));
    laplace_typed_trajectory_read(shape_ids, 8, shape_receive_structure, &read);
    pfree(shape_ids);
    shape_ids = shape_structure_ids(&read, false);
    hash128_t contract_types[2] = {read.call_type, read.input_type};
    types = hash128_array_from_ids(contract_types, 2);
    laplace_observation_read(shape_ids, NULL, types, 1, shape_receive_witness, &read);
    pfree(types); pfree(shape_ids);
    types = hash128_array_from_ids(&read.parse_type, 1);
    laplace_observation_read(parse_ids, NULL, types, 2, shape_receive_witness, &read);
    pfree(types); pfree(parse_ids);
    if (read.failed) goto done;
    count = hash_get_num_entries(read.witnesses);
    if ((Size) count > MaxAllocSize / sizeof(*rows))
        elog(ERROR, "task shape: witness set exceeds allocation capacity");
    rows = palloc(Max(count, 1) * sizeof(*rows));
    ArrayBuildState *subjects = NULL, *objects = NULL;
    hash_seq_init(&scan, read.witnesses);
    for (int i = 0; (row = hash_seq_search(&scan)) != NULL; ++i)
    {
        rows[i] = *row;
        Datum subject = hash128_to_datum(&row->subject), object = hash128_to_datum(&row->object);
        subjects = accumArrayResult(subjects, subject, false, BYTEAOID, read.owner);
        objects = accumArrayResult(objects, object, false, BYTEAOID, read.owner);
        pfree(DatumGetPointer(subject)); pfree(DatumGetPointer(object));
    }
    if (!count) { pfree(rows); goto done; }
    hash128_t all_types[4] = {read.example_type, read.call_type, read.input_type, read.parse_type};
    types = hash128_array_from_ids(all_types, 4);
    ArrayType *subject_ids = DatumGetArrayTypeP(makeArrayResult(subjects, read.owner));
    ArrayType *object_ids = DatumGetArrayTypeP(makeArrayResult(objects, read.owner));
    laplace_consensus_scan(subject_ids, object_ids, types, shape_receive_standing, &read, NULL);
    pfree(types); pfree(subject_ids); pfree(object_ids);
    qsort(rows, count, sizeof(*rows), laplace_prompt_witness_compare);
    shape_read_entity_types(&read);
    for (int i = 0; i < count && !read.failed; ++i)
    {
        const LaplaceObservation *example = rows + i;
        if (!hash128_eq(&example->type, &read.example_type) ||
            !shape_positive(&read, example, rows, count)) continue;
        ShapeStructure *shape = hash_search(read.structures, &example->object, HASH_FIND, NULL);
        ShapeStructure *exemplar = hash_search(read.structures, &example->subject, HASH_FIND, NULL);
        if (!shape || !shape->is_shape || !exemplar || !exemplar->is_parse ||
            !hash128_eq(&shape->shape.exemplar_parse, &exemplar->id) ||
            !laplace_prompt_relation_allowed(intent, &shape->shape.predicate)) continue;
        const LaplaceObservation *exemplar_witness = shape_parse_witness(&read, exemplar->id, rows, count);
        if (!exemplar_witness) continue;
        const LaplaceObservation *call;
        const LaplaceObservation **slots = palloc0(shape->shape.slot_count * sizeof(*slots));
        if (shape_contract(&read, shape, example, rows, count, &call, slots))
        {
            size_t *ordinals = palloc(shape->shape.slot_count * sizeof(*ordinals));
            int projection = laplace_task_shape_match_forms_view(&shape->shape, &exemplar->parse,
                structure->forms, structure->form_count, ordinals);
            if (projection < 0) elog(ERROR, "task shape: request projection allocation failed");
            bool conflict = false;
            int observed = 0;
            for (int j = 0; projection && j < structure->count; ++j)
                if (structure->parses[j]->supported)
                {
                    ++observed;
                    int match = laplace_task_shape_match_view(&shape->shape, &exemplar->parse,
                        &structure->parses[j]->decoded, ordinals);
                    if (match < 0) elog(ERROR, "task shape: observed structure constraint failed");
                    if (!match) conflict = true;
                }
            if (projection && conflict) intent->ambiguous = true;
            if (projection && !conflict)
            {
                if (!observed)
                    shape_instantiate(&read, shape, exemplar, NULL, example, call, slots, exemplar_witness);
                else
                    for (int j = 0; j < structure->count && !read.failed; ++j)
                        if (structure->parses[j]->supported)
                            shape_instantiate(&read, shape, exemplar, structure->parses[j], example,
                                              call, slots, exemplar_witness);
            }
            pfree(ordinals);
        }
        pfree(slots);
        CHECK_FOR_INTERRUPTS();
    }
    pfree(rows);
done:
    if (read.failed)
    {
        intent->budget_exhausted = true;
        /* A truncated operation set is discarded whole. */
        intent->relation_count = 0;
    }
    if (intent->relation_count > 0)
        intent->explicit_invocation = true;
    for (int i = 0; i < intent->relation_count; ++i)
        for (int j = i + 1; j < intent->relation_count; ++j)
            if (!hash128_eq(&intent->operations[i].result_relation, &intent->operations[j].result_relation) ||
                !shape_same_bindings(intent->operations + i, intent->operations + j))
                intent->ambiguous = true;
    /* Structures own memory-context callbacks, so their hash entries remain
     * alive until the same context releases their borrowed parse arrays. */
    hash_destroy(read.witnesses);
    hash_destroy(read.cells);
    hash_destroy(read.entity_types);
    MemoryContextSwitchTo(previous);
}
