#include "postgres.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "miscadmin.h"
#include "parser/parse_func.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/lsyscache.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/trajectory.h"
#include "content_membership_read.h"
#include "content_trajectory_read.h"
#include "consensus_scan.h"
#include "prompt_structure.h"
#include "spi_common.h"
#include "trajectory_wkb.h"
#include "walk_score.h"

typedef struct StructureParseIndex
{
    hash128_t id;
    LaplacePromptParse *parse;
} StructureParseIndex;

typedef struct StructureRead
{
    LaplacePromptStructure *state;
    const LaplacePromptInput *input;
    HTAB *ids;
    Oid as_binary;
    hash128_t *forms;
    int *origins;
    int form_count;
    int fanout;
    int witness_count;
    int candidate_count;
    ArrayBuildState *direct_ids;
} StructureRead;

typedef struct StructureConstituents
{
    hash128_t *ids;
    size_t count;
} StructureConstituents;

static int
receive_constituent(void *context, size_t ordinal, const hash128_t *id, uint64_t flags)
{
    StructureConstituents *output = context;
    if (ordinal == 0 || ordinal > output->count || flags != 0) return -1;
    output->ids[ordinal - 1] = *id;
    return 0;
}

static int
parse_order(const void *left, const void *right)
{
    const LaplacePromptParse *a = *(LaplacePromptParse *const *) left;
    const LaplacePromptParse *b = *(LaplacePromptParse *const *) right;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static int
witness_order(const void *left, const void *right)
{
    const LaplaceObservation *a = left, *b = right;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static void
release_parse(void *argument)
{
    LaplacePromptParse *parse = argument;
    laplace_ud_parse_free(&parse->decoded);
}

static void
receive_parse(Datum physicality, Datum entity, Datum geometry, void *context)
{
    StructureRead *read = context;
    hash128_t id = datum_to_hash128(entity);
    hash128_t physicality_id = datum_to_hash128(physicality), expected_physicality;
    laplace_physicality_id_compute(id, 8, &expected_physicality);
    if (!hash128_eq(&physicality_id, &expected_physicality))
        elog(ERROR, "structural coupling requires canonical typed physicality identity");
    bool found;
    StructureParseIndex *index = hash_search(read->ids, &id, HASH_ENTER, &found);
    if (found) return;
    index->parse = NULL;
    if (read->candidate_count >= read->fanout)
    {
        read->state->budget_exhausted = true;
        return;
    }
    ++read->candidate_count;
    MemoryContext previous = MemoryContextSwitchTo(read->state->owner);
    bytea *wkb = DatumGetByteaP(OidFunctionCall1(read->as_binary, geometry));
    uint32 vertices;
    const unsigned char *packed = laplace_trajectory_wkb_points(wkb, &vertices);
    if ((Size) vertices > MaxAllocSize / (4 * sizeof(double)))
        elog(ERROR, "structural coupling trajectory exceeds allocation capacity");
    double *aligned = palloc(Max((Size) vertices, 1) * 4 * sizeof(double));
    memcpy(aligned, packed, (Size) vertices * 4 * sizeof(double));
    size_t length;
    if (trajectory_constituent_count(aligned, vertices, &length) != 0 ||
        length > INT_MAX || length > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "structural coupling requires a complete valid packed trajectory");
    hash128_t *flat = palloc(Max(length, 1) * sizeof(hash128_t));
    StructureConstituents output = {flat, length};
    if (trajectory_visit_constituents(aligned, vertices, receive_constituent, &output) != 0)
        elog(ERROR, "structural coupling cannot decode the complete trajectory");
    pfree(aligned);
    pfree(wkb);

    laplace_ud_parse_t decoded = {0};
    laplace_ud_parse_status_t status = laplace_ud_parse_decode(flat, length, &decoded);
    if (status == LAPLACE_UD_PARSE_MEMORY)
        elog(ERROR, "structural coupling cannot allocate the complete decoded parse");
    hash128_t canonical;
    hash128_merkle(4, flat, length, &canonical);
    if (!hash128_eq(&canonical, &id))
    {
        laplace_ud_parse_free(&decoded);
        pfree(flat);
        MemoryContextSwitchTo(previous);
        return;
    }
    LaplacePromptParse *parse = palloc0(sizeof(*parse));
    parse->id = id;
    parse->physicality = datum_to_hash128(physicality);
    if (length > 0) parse->schema_id = flat[0];
    parse->constituents = flat;
    parse->constituent_count = length;
    parse->decoded = decoded;
    parse->decode_status = status;
    parse->cleanup.func = release_parse;
    parse->cleanup.arg = parse;
    MemoryContextRegisterResetCallback(read->state->owner, &parse->cleanup);
    parse->token_origins = palloc(Max(decoded.token_count, 1) * sizeof(int));
    parse->aligned = status == LAPLACE_UD_PARSE_OK &&
                     decoded.token_count == (size_t) read->form_count &&
                     decoded.mwt_count == 0;
    for (size_t i = 0; i < decoded.token_count; ++i)
    {
        parse->token_origins[i] = -1;
        if (i < (size_t) read->form_count &&
            hash128_eq(&decoded.tokens[i].form_id, &read->forms[i]))
            parse->token_origins[i] = read->origins[i];
        else
            parse->aligned = false;
    }
    index->parse = parse;
    if (read->state->count == read->state->capacity)
    {
        Size ceiling = Min((Size) INT_MAX, MaxAllocSize / sizeof(*read->state->parses));
        if ((Size) read->state->capacity >= ceiling)
            elog(ERROR, "structural coupling parse set exceeds allocation capacity");
        int capacity = (int) Min(ceiling, read->state->capacity ?
                               (Size) read->state->capacity * 2 : 16);
        read->state->parses = read->state->parses ?
            repalloc(read->state->parses, capacity * sizeof(*read->state->parses)) :
            palloc(capacity * sizeof(*read->state->parses));
        read->state->capacity = capacity;
    }
    read->state->parses[read->state->count++] = parse;
    MemoryContextSwitchTo(previous);
}

static void
receive_direct_parse(const LaplaceConsensusRow *row, void *context)
{
    StructureRead *read = context;
    if (row->object_is_null) return;
    if (read->direct_ids && read->direct_ids->nelems >= read->fanout)
    {
        read->state->budget_exhausted = true;
        return;
    }
    MemoryContext previous = MemoryContextSwitchTo(read->state->owner);
    Datum id = hash128_to_datum(&row->object);
    read->direct_ids = accumArrayResult(read->direct_ids, id, false, BYTEAOID,
                                       read->state->owner);
    pfree(DatumGetPointer(id));
    MemoryContextSwitchTo(previous);
}

static void
receive_witness(int ordinal, int16 role, const LaplaceObservation *row, void *context)
{
    StructureRead *read = context;
    (void) ordinal;
    if (role != 2 || row->object_null) return;
    StructureParseIndex *index = hash_search(read->ids, &row->object, HASH_FIND, NULL);
    if (!index || !index->parse) return;
    LaplacePromptParse *parse = index->parse;
    if (parse->decode_status == LAPLACE_UD_PARSE_OK &&
        !hash128_eq(&row->subject, &parse->decoded.sentence_id)) return;
    if (read->witness_count >= read->fanout)
    {
        read->state->budget_exhausted = true;
        return;
    }
    MemoryContext previous = MemoryContextSwitchTo(read->state->owner);
    if (parse->witness_count == parse->witness_capacity)
    {
        Size ceiling = Min((Size) INT_MAX, MaxAllocSize / sizeof(*parse->witnesses));
        if ((Size) parse->witness_capacity >= ceiling)
            elog(ERROR, "structural coupling witness set exceeds allocation capacity");
        int capacity = (int) Min(ceiling, parse->witness_capacity ?
                               (Size) parse->witness_capacity * 2 : 4);
        parse->witnesses = parse->witnesses ?
            repalloc(parse->witnesses, capacity * sizeof(*parse->witnesses)) :
            palloc(capacity * sizeof(*parse->witnesses));
        parse->witness_capacity = capacity;
    }
    parse->witnesses[parse->witness_count++] = *row;
    ++read->witness_count;
    MemoryContextSwitchTo(previous);
}

static void
receive_standing(const LaplaceConsensusRow *row, void *context)
{
    StructureRead *read = context;
    if (row->object_is_null) return;
    StructureParseIndex *index = hash_search(read->ids, &row->object, HASH_FIND, NULL);
    if (!index || !index->parse) return;
    LaplacePromptParse *parse = index->parse;
    if (parse->decode_status == LAPLACE_UD_PARSE_OK &&
        hash128_eq(&row->subject, &parse->decoded.sentence_id))
        parse->positive_standing = laplace_walk_edge_weight(row->rating, row->rd) > 0.0;
}

static bool
has_uncontested_witness(const LaplacePromptParse *parse)
{
    for (int i = 0; i < parse->witness_count; ++i)
    {
        const LaplaceObservation *support = &parse->witnesses[i];
        if (support->outcome != LAPLACE_ATTESTATION_OUTCOME_CONFIRM ||
            support->occurrences <= 0 || support->source_null || support->context_null)
            continue;
        bool refuted = false;
        for (int j = 0; j < parse->witness_count; ++j)
        {
            const LaplaceObservation *against = &parse->witnesses[j];
            if (against->outcome == LAPLACE_ATTESTATION_OUTCOME_REFUTE &&
                against->occurrences > 0 && !against->source_null && !against->context_null &&
                hash128_eq(&support->source, &against->source) &&
                hash128_eq(&support->context, &against->context))
                refuted = true;
        }
        if (!refuted) return true;
    }
    return false;
}

LaplacePromptStructure *
laplace_prompt_structure_couple(const LaplacePromptInput *input,
                               ArrayType *relation_types, int fanout,
                               MemoryContext owner)
{
    MemoryContext previous = MemoryContextSwitchTo(owner);
    LaplacePromptStructure *state = palloc0(sizeof(*state));
    state->owner = owner;
    state->root = input->root;
    hash128_t has_parse;
    if (laplace_relation_resolve("HAS_PARSE", &has_parse) != 0)
        elog(ERROR, "structural coupling requires governed HAS_PARSE relation");
    if (relation_types)
    {
        bool allowed = false;
        ArrayIterator iterator = array_create_iterator(relation_types, 0, NULL);
        Datum value;
        bool isnull;
        while (array_iterate(iterator, &value, &isnull))
        {
            if (isnull) continue;
            hash128_t relation = datum_to_hash128(value);
            if (hash128_eq(&relation, &has_parse)) allowed = true;
        }
        array_free_iterator(iterator);
        if (!allowed)
        {
            MemoryContextSwitchTo(previous);
            return state;
        }
    }
    if (fanout <= 0)
    {
        state->budget_exhausted = true;
        MemoryContextSwitchTo(previous);
        return state;
    }
    StructureRead read = {.state = state, .input = input, .fanout = fanout};
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(StructureParseIndex);
    ctl.hcxt = owner;
    read.ids = hash_create("coupled structural identities", Min(fanout, 128), &ctl,
                           HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    Oid physicalities = get_relname_relid("physicalities", get_namespace_oid("laplace", false));
    Oid geometry = get_atttype(physicalities, get_attnum(physicalities, "trajectory"));
    read.as_binary = LookupFuncName(list_make2(makeString("public"), makeString("st_asbinary")),
                                   1, &geometry, false);
    Datum *nodes;
    bool *nulls;
    int count;
    deconstruct_array(input->nodes, INT4OID, 4, true, TYPALIGN_INT, &nodes, &nulls, &count);
    read.forms = palloc(Max(count, 1) * sizeof(*read.forms));
    read.origins = palloc(Max(count, 1) * sizeof(*read.origins));
    size_t text_length;
    const uint8_t *text = tier_tree_text(input->tree, &text_length);
    for (int i = 0; i < count; ++i)
    {
        tier_node_view_t node;
        if (nulls[i] || tier_tree_get_node(input->tree, DatumGetInt32(nodes[i]), &node) != 0 ||
            (uint64) node.text_range_off + node.text_range_len > text_length)
            elog(ERROR, "structural coupling requires complete current occurrence positions");
        /* Whitespace is a Unicode floor property. Every punctuation mark and
         * every other content occurrence remains in the ordered alignment. */
        if (laplace_text_is_all_whitespace(text + node.text_range_off, node.text_range_len))
            continue;
        read.forms[read.form_count] = node.id;
        read.origins[read.form_count++] = i;
    }
    pfree(nodes);
    pfree(nulls);
    state->forms = read.forms;
    state->origins = read.origins;
    state->form_count = read.form_count;
    ArrayType *types = hash128_array_from_ids(&has_parse, 1);
    ArrayType *root = hash128_array_from_ids(&input->root, 1);
    laplace_consensus_scan(root, NULL, types, receive_direct_parse, &read, NULL);
    if (read.direct_ids)
    {
        ArrayType *ids = DatumGetArrayTypeP(makeArrayResult(read.direct_ids, owner));
        laplace_typed_trajectory_read(ids, 8, receive_parse, &read);
        pfree(ids);
    }
    pfree(root);
    ArrayType *forms = hash128_array_from_ids(read.forms, read.form_count);
    laplace_ud_markers_t parse_markers;
    laplace_ud_markers_init(&parse_markers);
    ArrayType *required = hash128_array_from_ids(&parse_markers.schema_v1, 1);
    bool complete = laplace_typed_membership_read_with_required(forms, true,
        required, 8, fanout, receive_parse, &read);
    pfree(required);
    state->budget_exhausted = state->budget_exhausted || !complete;
    if (state->count > 0)
    {
        hash128_t *ids = palloc(state->count * sizeof(*ids));
        for (int i = 0; i < state->count; ++i) ids[i] = state->parses[i]->id;
        ArrayType *parse_ids = hash128_array_from_ids(ids, state->count);
        laplace_observation_read(parse_ids, NULL, types, 2, receive_witness, &read);
        laplace_consensus_scan(NULL, parse_ids, types, receive_standing, &read, NULL);
        qsort(state->parses, state->count, sizeof(*state->parses), parse_order);
        int supported = 0;
        for (int i = 0; i < state->count; ++i)
        {
            LaplacePromptParse *parse = state->parses[i];
            if (parse->witness_count > 1)
                qsort(parse->witnesses, parse->witness_count,
                       sizeof(*parse->witnesses), witness_order);
            parse->supported = !state->budget_exhausted && parse->aligned &&
                parse->positive_standing && has_uncontested_witness(parse);
            if (parse->supported) ++supported;
        }
        state->ambiguous = supported > 1;
        pfree(parse_ids);
        pfree(ids);
    }
    pfree(types);
    pfree(forms);
    hash_destroy(read.ids);
    MemoryContextSwitchTo(previous);
    return state;
}
