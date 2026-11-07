#include "task_shape_internal.h"

static int
shape_receive_constituent(void *context, size_t ordinal, const hash128_t *id, uint64_t flags)
{
    ShapeConstituents *output = context;
    if (ordinal == 0 || ordinal > output->count) return -1;
    output->ids[ordinal - 1] = *id;
    output->flags[ordinal - 1] = flags;
    return 0;
}

HTAB *
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

void
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
    uint64_t *flags = palloc(Max(count, 1) * sizeof(uint64_t));
    ShapeConstituents output = {flat, flags, count};
    if (trajectory_visit_constituents(aligned, vertices, shape_receive_constituent, &output) != 0)
        elog(ERROR, "task shape: trajectory decode failed");
    pfree(aligned);
    pfree(wkb);
    /* A parse trajectory is either flat (unflagged constituents, Merkle id) or
     * vertex-flagged: every constituent a PARSE vertex, re-identified from its
     * units and kept only when that id equals the entity id. */
    bool vertex_layout = count > 0, flat_layout = true;
    for (size_t i = 0; i < count; ++i)
    {
        if (!laplace_vflag_is_parse(flags[i])) vertex_layout = false;
        if (flags[i] != 0) flat_layout = false;
    }
    hash128_t canonical;
    hash128_zero(&canonical);
    int status;
    if (vertex_layout)
    {
        status = laplace_ud_parse_from_vertices(flat, flags, count, &record->parse, &canonical);
        if (status == LAPLACE_UD_PARSE_MEMORY)
            elog(ERROR, "task shape: exemplar allocation failed");
        if (status == LAPLACE_UD_PARSE_OK && !hash128_eq(&canonical, &id))
        {
            laplace_ud_parse_free(&record->parse);
            status = LAPLACE_UD_PARSE_SCHEMA;
        }
        pfree(flags);
        if (status != LAPLACE_UD_PARSE_OK)
        {
            pfree(flat);
            MemoryContextSwitchTo(previous);
            return;
        }
        record->flat = flat;
        record->length = count;
        record->is_parse = true;
        record->cleanup.func = shape_release_parse;
        record->cleanup.arg = record;
        MemoryContextRegisterResetCallback(read->owner, &record->cleanup);
        MemoryContextSwitchTo(previous);
        return;
    }
    pfree(flags);
    if (count == 1)
        canonical = flat[0];
    else if (count > 1)
        hash128_merkle(4, flat, count, &canonical);
    if (!flat_layout || read->vertex_only || !hash128_eq(&canonical, &id))
    {
        /* The vertex-only read drops what it does not keep so a later typed read
         * can still admit the same entity. */
        if (read->vertex_only) hash_search(read->structures, &id, HASH_REMOVE, NULL);
        pfree(flat);
        MemoryContextSwitchTo(previous);
        return;
    }
    record->flat = flat;
    record->length = count;
    status = laplace_task_shape_decode_view(flat, count, &record->shape);
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

void
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
        hash128_t unnamed;
        hash128_zero(&unnamed);
        /* A vertex-flagged parse names no sentence; its HAS_PARSE subject is the sentence. */
        if (role != 2 || !record || !record->is_parse ||
            (!hash128_eq(&record->parse.sentence_id, &unnamed) &&
             !hash128_eq(&record->parse.sentence_id, &row->subject))) return;
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

void
shape_receive_standing(const LaplaceConsensusRow *row, void *opaque)
{
    ShapeRead *read = opaque;
    if (row->object_is_null) return;
    LaplaceObservationCell key = {row->subject, row->type, row->object};
    ShapeCell *cell = hash_search(read->cells, &key, HASH_FIND, NULL);
    if (cell) cell->positive = laplace_walk_edge_weight(row->rating, row->rd) > 0.0;
}

bool
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

const LaplaceObservation *
shape_parse_witness(const ShapeRead *read, hash128_t parse,
                    const LaplaceObservation *rows, int count)
{
    for (int i = 0; i < count; ++i)
        if (hash128_eq(&rows[i].type, &read->parse_type) &&
            hash128_eq(&rows[i].object, &parse) && shape_positive(read, &rows[i], rows, count))
            return rows + i;
    return NULL;
}

const LaplaceObservation *
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

ArrayType *
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

/* One prepared set read of the stored entity types of every COUPLE binding.
 * Slot compatibility is decided by these types, never by labels. */
void
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
        plan = SPI_prepare_cursor(sql, 1, types, CURSOR_OPT_PARALLEL_OK);
        if (!plan || SPI_keepplan(plan) != 0)
            elog(ERROR, "task shape: failed to prepare typed input set read");
    }
    Datum args[1] = {makeArrayResult(ids, read->owner)};
    if (SPI_execute_plan(plan, args, NULL, true, 0) != SPI_OK_SELECT)
        elog(ERROR, "task shape: typed input set read failed");
    for (uint64 i = 0; i < SPI_processed; ++i)
    {
        bool id_null, type_null;
        Datum id_value = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 1, &id_null);
        Datum type_value = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 2, &type_null);
        if (id_null || type_null) continue;
        ShapeEntityType key = {datum_to_hash128(id_value), datum_to_hash128(type_value)};
        (void) hash_search(read->entity_types, &key, HASH_ENTER, NULL);
        CHECK_FOR_INTERRUPTS();
    }
    SPI_freetuptable(SPI_tuptable);
    pfree(DatumGetPointer(args[0]));
}
