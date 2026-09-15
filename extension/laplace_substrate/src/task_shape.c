#include "postgres.h"

#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "miscadmin.h"
#include "parser/parse_func.h"
#include "lib/stringinfo.h"
#include "utils/lsyscache.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/sql_catalog.h"
#include "laplace/core/task_shape.h"
#include "laplace/core/trajectory.h"
#include "content_membership_read.h"
#include "content_trajectory_read.h"
#include "prompt_intent.h"
#include "prompt_structure.h"
#include "task_shape.h"
#include "trajectory_wkb.h"

typedef struct ShapeStructure
{
    hash128_t id;
    hash128_t *flat;
    size_t length;
    laplace_ud_parse_t parse;
    laplace_task_shape_t shape;
    bool is_parse, is_shape;
    MemoryContextCallback cleanup;
} ShapeStructure;

typedef struct ShapeCell
{
    LaplaceObservationCell key;
    bool positive;
} ShapeCell;

typedef struct ShapeEntityType
{
    hash128_t id, type;
    bool known, conflicting;
} ShapeEntityType;

typedef struct ShapeRead
{
    LaplacePromptIntent *intent;
    MemoryContext owner;
    HTAB *structures, *witnesses, *cells, *entity_types;
    hash128_t example_type, call_type, input_type, parse_type;
    Oid as_binary;
    int fanout;
    bool failed;
} ShapeRead;

static HTAB *
shape_table(const char *name, Size key, Size entry, MemoryContext owner)
{
    HASHCTL ctl = {0};
    ctl.keysize = key;
    ctl.entrysize = entry;
    ctl.hcxt = owner;
    return hash_create(name, 128, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static void
shape_release_parse(void *argument)
{
    ShapeStructure *record = argument;
    laplace_ud_parse_free(&record->parse);
}

static void
shape_receive_structure(Datum physicality, Datum entity, Datum geometry, void *opaque)
{
    ShapeRead *read = opaque;
    hash128_t id = datum_to_hash128(entity);
    hash128_t expected_physicality, actual_physicality = datum_to_hash128(physicality);
    laplace_physicality_id_compute(id, 8, &expected_physicality);
    if (!hash128_eq(&expected_physicality, &actual_physicality)) return;
    bool found;
    ShapeStructure *record = hash_search(read->structures, &id, HASH_ENTER, &found);
    MemoryContext previous;
    if (found) return;
    previous = MemoryContextSwitchTo(read->owner);
    MemSet(((char *) record) + sizeof(record->id), 0, sizeof(*record) - sizeof(record->id));
    bytea *wkb = DatumGetByteaP(OidFunctionCall1(read->as_binary, geometry));
    uint32 vertices;
    const unsigned char *packed = laplace_trajectory_wkb_points(wkb, &vertices);
    if ((Size) vertices > MaxAllocSize / (4 * sizeof(double)))
        elog(ERROR, "task shape: trajectory exceeds allocation capacity");
    double *aligned = palloc(Max((Size) vertices, 1) * 4 * sizeof(double));
    memcpy(aligned, packed, (Size) vertices * 4 * sizeof(double));
    size_t count;
    if (trajectory_constituent_count(aligned, vertices, &count) != 0 ||
        count > INT_MAX || count > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "task shape: invalid complete trajectory");
    hash128_t *flat = palloc(Max(count, 1) * sizeof(hash128_t));
    if (trajectory_constituents(aligned, vertices, flat, count) != (int) count)
        elog(ERROR, "task shape: trajectory decode failed");
    pfree(aligned);
    pfree(wkb);
    hash128_t canonical;
    hash128_merkle(4, flat, count, &canonical);
    if (!hash128_eq(&canonical, &id))
    {
        pfree(flat);
        MemoryContextSwitchTo(previous);
        return;
    }
    record->flat = flat;
    record->length = count;
    int status = laplace_task_shape_decode(flat, count, &record->shape);
    if (status == -3) elog(ERROR, "task shape: codec allocation failed");
    record->is_shape = status == 0;
    if (!record->is_shape)
    {
        status = laplace_ud_parse_decode(flat, count, &record->parse);
        if (status == LAPLACE_UD_PARSE_MEMORY)
            elog(ERROR, "task shape: exemplar allocation failed");
        record->is_parse = status == LAPLACE_UD_PARSE_OK;
        if (record->is_parse)
        {
            record->cleanup.func = shape_release_parse;
            record->cleanup.arg = record;
            MemoryContextRegisterResetCallback(read->owner, &record->cleanup);
        }
    }
    MemoryContextSwitchTo(previous);
}

static void
shape_receive_witness(int ordinal, int16 role, const LaplaceObservation *row, void *opaque)
{
    ShapeRead *read = opaque;
    bool found;
    (void) ordinal;
    if (row->object_null || row->source_null || row->context_null || row->occurrences <= 0)
        return;
    if (hash128_eq(&row->type, &read->parse_type))
    {
        ShapeStructure *record = hash_search(read->structures, &row->object, HASH_FIND, NULL);
        if (role != 2 || !record || !record->is_parse ||
            !hash128_eq(&record->parse.sentence_id, &row->subject)) return;
    }
    else if (role != 1) return;
    if (!hash_search(read->witnesses, &row->id, HASH_FIND, NULL) &&
        hash_get_num_entries(read->witnesses) >= read->fanout)
    {
        read->failed = true;
        return;
    }
    MemoryContext previous = MemoryContextSwitchTo(read->owner);
    LaplaceObservation *stored = hash_search(read->witnesses, &row->id, HASH_ENTER, NULL);
    *stored = *row;
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    ShapeCell *cell = hash_search(read->cells, &key, HASH_ENTER, &found);
    if (!found) cell->positive = false;
    MemoryContextSwitchTo(previous);
}

static void
shape_receive_standing(const LaplaceConsensusRow *row, void *opaque)
{
    ShapeRead *read = opaque;
    if (row->object_is_null) return;
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    ShapeCell *cell = hash_search(read->cells, &key, HASH_FIND, NULL);
    if (cell) cell->positive = laplace_walk_edge_weight(row->rating, row->rd) > 0.0;
}

static bool
shape_positive(const ShapeRead *read, const LaplaceObservation *row,
               const LaplaceObservation *rows, int count)
{
    if (row->outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM || row->occurrences <= 0 ||
        row->source_null || row->context_null || row->object_null) return false;
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    ShapeCell *cell = hash_search(read->cells, &key, HASH_FIND, NULL);
    if (!cell || !cell->positive) return false;
    for (int i = 0; i < count; ++i)
        if (rows[i].outcome == LAPLACE_ATTESTATION_OUTCOME_REFUTE &&
            hash128_eq(&row->subject, &rows[i].subject) &&
            hash128_eq(&row->type, &rows[i].type) &&
            hash128_eq(&row->object, &rows[i].object) &&
            laplace_prompt_same_scope(row, &rows[i])) return false;
    return true;
}

static const LaplaceObservation *
shape_parse_witness(const ShapeRead *read, hash128_t parse,
                    const LaplaceObservation *rows, int count)
{
    for (int i = 0; i < count; ++i)
        if (hash128_eq(&rows[i].type, &read->parse_type) &&
            hash128_eq(&rows[i].object, &parse) && shape_positive(read, &rows[i], rows, count))
            return rows + i;
    return NULL;
}

static const LaplaceObservation *
shape_current_witness(const LaplacePromptParse *parse)
{
    const LaplaceObservation *best = NULL;
    for (int i = 0; i < parse->witness_count; ++i)
    {
        const LaplaceObservation *row = parse->witnesses + i;
        if (row->outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM || row->occurrences <= 0 ||
            row->source_null || row->context_null || row->object_null) continue;
        bool refuted = false;
        for (int j = 0; j < parse->witness_count; ++j)
        {
            const LaplaceObservation *against = parse->witnesses + j;
            if (against->outcome == LAPLACE_ATTESTATION_OUTCOME_REFUTE &&
                against->occurrences > 0 && !against->source_null && !against->context_null &&
                laplace_prompt_same_scope(row, against)) refuted = true;
        }
        if (!refuted && (!best || memcmp(&row->id, &best->id, sizeof(hash128_t)) < 0)) best = row;
    }
    return best;
}

static ArrayType *
shape_structure_ids(ShapeRead *read, bool parses)
{
    HASH_SEQ_STATUS scan;
    ShapeStructure *record;
    ArrayBuildState *ids = NULL;
    hash_seq_init(&scan, read->structures);
    while ((record = hash_seq_search(&scan)) != NULL)
        if (parses ? record->is_parse : record->is_shape)
        {
            Datum id = hash128_to_datum(&record->id);
            ids = accumArrayResult(ids, id, false, BYTEAOID, read->owner);
            pfree(DatumGetPointer(id));
        }
    return ids ? DatumGetArrayTypeP(makeArrayResult(ids, read->owner)) :
                 construct_empty_array(BYTEAOID);
}

/* One fixed prepared read for the entire already coupled semantic estate.
 * Stored entity typing supplies slot compatibility; labels never do. */
static void
shape_read_entity_types(ShapeRead *read)
{
    static SPIPlanPtr plan = NULL;
    HASH_SEQ_STATUS scan;
    LaplacePromptIntentBinding *binding;
    ArrayBuildState *ids = NULL;
    hash_seq_init(&scan, read->intent->bindings);
    while ((binding = hash_seq_search(&scan)) != NULL)
    {
        Datum id = hash128_to_datum(&binding->id);
        ids = accumArrayResult(ids, id, false, BYTEAOID, read->owner);
        pfree(DatumGetPointer(id));
    }
    if (!ids) return;
    if (!plan)
    {
        Oid types[1] = {BYTEAARRAYOID};
        const char *sql = laplace_sql_query_text("generation.task_shape_entity_types");
        if (!sql) elog(ERROR, "task shape: typed input query is absent from native catalog");
        plan = SPI_prepare(sql, 1, types);
        if (!plan || SPI_keepplan(plan) != 0)
            elog(ERROR, "task shape: failed to prepare typed input set read");
    }
    Datum args[1] = {makeArrayResult(ids, read->owner)};
    if (SPI_execute_plan(plan, args, NULL, true, 0) != SPI_OK_SELECT)
        elog(ERROR, "task shape: typed input set read failed");
    for (uint64 i = 0; i < SPI_processed; ++i)
    {
        bool id_null, type_null, found;
        Datum id_value = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 1, &id_null);
        Datum type_value = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 2, &type_null);
        if (id_null || type_null) continue;
        hash128_t id = datum_to_hash128(id_value), type = datum_to_hash128(type_value);
        ShapeEntityType *entry = hash_search(read->entity_types, &id, HASH_ENTER, &found);
        if (!found) { entry->known = true; entry->conflicting = false; entry->type = type; }
        else if (!hash128_eq(&entry->type, &type)) entry->conflicting = true;
        CHECK_FOR_INTERRUPTS();
    }
    SPI_freetuptable(SPI_tuptable);
    pfree(DatumGetPointer(args[0]));
}

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
                if (hash128_eq(&row->object, shape->shape.slots + slot * 3)) break;
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

typedef struct SlotChoices
{
    hash128_t *ids;
    int count;
    int origin;
} SlotChoices;

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

/* A derived request projection is a calculation recipe, never a fabricated
 * HAS_PARSE observation. Morphological lemmas are not guessed from concept IDs. */
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
        laplace_task_shape_match(&shape->shape, &exemplar->parse, &current->decoded, ordinals) :
        laplace_task_shape_match_forms(&shape->shape, &exemplar->parse,
            read->intent->structure->forms, read->intent->structure->form_count, ordinals);
    if (match < 0) elog(ERROR, "task shape: exact structural matching failed");
    if (!match) { pfree(ordinals); return; }
    const LaplaceObservation *parse_witness = current ? shape_current_witness(current) : NULL;
    if (current && !parse_witness) { pfree(ordinals); return; }
    SlotChoices *choices = palloc0(slots * sizeof(*choices));
    int *selected = palloc0(slots * sizeof(*selected));
    uint64 combinations = 1;
    for (size_t slot = 0; slot < slots; ++slot)
    {
        HASH_SEQ_STATUS scan;
        LaplacePromptIntentBinding *binding;
        int count = 0;
        choices[slot].origin = current ? current->token_origins[ordinals[slot]] :
            read->intent->structure->origins[ordinals[slot]];
        if (choices[slot].origin < 0) { combinations = 0; break; }
        if ((Size) read->fanout > MaxAllocSize / sizeof(hash128_t))
            elog(ERROR, "task shape: input candidate envelope exceeds allocation capacity");
        choices[slot].ids = palloc((Size) Max(read->fanout, 1) * sizeof(hash128_t));
        hash_seq_init(&scan, read->intent->bindings);
        while ((binding = hash_seq_search(&scan)) != NULL)
        {
            if (!bms_is_member(choices[slot].origin, binding->origins)) continue;
            ShapeEntityType *type = hash_search(read->entity_types, &binding->id, HASH_FIND, NULL);
            if (!type || !type->known || type->conflicting ||
                !hash128_eq(&type->type, shape->shape.slots + slot * 3 + 2)) continue;
            if (count >= read->fanout) { read->failed = true; continue; }
            choices[slot].ids[count++] = binding->id;
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
    read.entity_types = shape_table("task shape entity types", sizeof(hash128_t), sizeof(ShapeEntityType), read.owner);
    /* These are the declared fields of the source-shape protocol. Resolve
     * registry metadata once at the boundary; no rendered cue or output label
     * supplies a relation, and aliases cannot silently change this schema. */
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
    read.failed = !laplace_typed_membership_read(members, false, 8, fanout, shape_receive_structure, &read);
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
            int projection = laplace_task_shape_match_forms(&shape->shape, &exemplar->parse,
                structure->forms, structure->form_count, ordinals);
            if (projection < 0) elog(ERROR, "task shape: request projection allocation failed");
            bool conflict = false;
            int observed = 0;
            for (int j = 0; projection && j < structure->count; ++j)
                if (structure->parses[j]->supported)
                {
                    ++observed;
                    int match = laplace_task_shape_match(&shape->shape, &exemplar->parse,
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
        /* No retained prefix is a smaller declaration or a completed program. */
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
