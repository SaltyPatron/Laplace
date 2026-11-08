#include "postgres.h"

#include <limits.h>

#include "catalog/pg_type.h"
#include "lib/stringinfo.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "laplace/core/attestation_engine.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/tier_tree.h"

#include "cognition_program_internal.h"
#include "consensus_scan.h"
#include "spi_common.h"
#include "walk_score.h"

static void
program_reserve_output(LaplaceCognitionProgram *program)
{
    int capacity;

    if (program->output_count < program->output_capacity)
        return;
    if (program->output_capacity == 0)
        capacity = 16;
    else
    {
        int64 grown = (int64) program->output_capacity * 2;
        if (grown > INT_MAX)
            elog(ERROR, "cognition program: output count exceeds int capacity");
        capacity = (int) grown;
    }
    if ((Size) capacity > MaxAllocSize / sizeof(hash128_t) ||
        (Size) capacity > MaxAllocSize / sizeof(bool) ||
        (Size) capacity > MaxAllocSize / sizeof(Bitmapset *))
        elog(ERROR, "cognition program: output set exceeds allocation capacity");

    if (program->output_capacity == 0)
    {
        program->outputs = palloc(sizeof(hash128_t) * (Size) capacity);
        program->output_semantic_support = palloc(sizeof(bool) * (Size) capacity);
        program->output_origins = palloc(sizeof(Bitmapset *) * (Size) capacity);
    }
    else
    {
        program->outputs = repalloc(program->outputs,
                                    sizeof(hash128_t) * (Size) capacity);
        program->output_semantic_support = repalloc(program->output_semantic_support,
                                                     sizeof(bool) * (Size) capacity);
        program->output_origins = repalloc(program->output_origins,
                                           sizeof(Bitmapset *) * (Size) capacity);
    }
    program->output_capacity = capacity;
}

static HTAB *
semantic_origin_index(MemoryContext owner, int expected)
{
    HASHCTL ctl = {0};
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(SemanticOriginEntry);
    ctl.hcxt = owner;
    return hash_create("cognition semantic origins", Max(expected, 16), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static SemanticOriginEntry *
semantic_origin_get(LaplaceCognitionProgram *program, const hash128_t *id,
                    bool create)
{
    bool found = false;
    SemanticOriginEntry *entry = hash_search(
        program->semantic_origins, id, create ? HASH_ENTER : HASH_FIND,
        create ? &found : NULL);
    if (create && entry && !found)
        entry->origins = NULL;
    return entry;
}

static void
semantic_origin_add(LaplaceCognitionProgram *program, const hash128_t *id,
                    int origin)
{
    SemanticOriginEntry *entry = semantic_origin_get(program, id, true);
    entry->origins = bms_add_member(entry->origins, origin);
}

static void
record_semantic_channel(LaplaceCognitionProgram *program,
                        const LaplaceQueryChannel *channel)
{
    SemanticOriginEntry *anchor;
    SemanticOriginEntry *candidate;
    const laplace_relation_def_t *def = NULL;

    /* A channel anchored by a geometry neighbor is fingerprinted but never
     * propagates semantic ancestry: proximity is not testimony. */
    if (channel->operand_role == LAPLACE_QUERY_OPERAND_GEOMETRY)
        return;

    if (!(laplace_walk_edge_weight(channel->rating, channel->rd) > 0.0) ||
        !(channel->outbound ||
          (laplace_relation_lookup(&channel->relation, &def) == 0 &&
           def != NULL && def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC)) ||
        laplace_prompt_contract_relation(&channel->relation))
        return;

    anchor = semantic_origin_get(program, &channel->anchor, false);
    if (!anchor || !anchor->origins)
        return;
    /* A channel of a declared operation's result relation, anchored at one of
     * its exact inputs, marks the candidate as answering that input. */
    bool declared_result = false;
    for (int i = 0; i < program->operation_relation_count; ++i)
    {
        const LaplacePromptRelationRead *operation = &program->operations[i];
        const LaplacePromptOperand *input;
        SemanticOriginEntry *result;
        bool found;
        if (!hash128_eq(&operation->result_relation, &channel->relation)) continue;
        input = laplace_prompt_operation_input(operation, &channel->anchor);
        if (!input) continue;
        result = hash_search(program->invocation_results, &channel->candidate, HASH_ENTER, &found);
        if (!found) result->origins = NULL;
        result->origins = bms_add_member(result->origins, input->requirement);
        declared_result = true;
    }
    if (!declared_result && !walk_relation_salient(channel->relation))
        return;
    candidate = semantic_origin_get(program, &channel->candidate, true);
    candidate->origins = bms_add_members(candidate->origins, anchor->origins);
}

bool
laplace_upos_is_content(const hash128_t *upos)
{
    static const char *const content_tags[] = {
        "NOUN", "PROPN", "VERB", "ADJ", "ADV", "NUM", "INTJ"
    };
    for (size_t i = 0; i < sizeof(content_tags) / sizeof(content_tags[0]); ++i)
    {
        hash128_t id;
        if (laplace_pos_resolve_entity(
                content_tags[i], LAPLACE_POS_TAGSET_UPOS, &id) == 0 &&
            hash128_eq(upos, &id))
            return true;
    }
    return false;
}

/* Obligations from supported, aligned parses of the observation: the
 * occurrences whose parsed UPOS is a content tag. NULL when no parse is
 * supported, leaving every eligible occurrence required. Only obligation
 * closure is narrowed; the whole observation still shapes the program id. */
static Bitmapset *
program_content_origins(const LaplacePromptIntent *intent, int prompt_origin_count)
{
    Bitmapset *required = NULL;
    bool supported = false;

    if (!intent || !intent->structure)
        return NULL;
    for (int p = 0; p < intent->structure->count; ++p)
    {
        const LaplacePromptParse *parse = intent->structure->parses[p];
        if (!parse || !parse->supported || !parse->aligned)
            continue;
        supported = true;
        for (size_t t = 0; t < parse->decoded.token_count; ++t)
        {
            int origin = parse->token_origins[t];
            if (origin < 0 || origin >= prompt_origin_count)
                continue;
            if (laplace_upos_is_content(&parse->decoded.tokens[t].upos_id))
                required = bms_add_member(required, origin);
        }
    }
    return supported && required ? required : NULL;
}

/*
 * Without a supported parse, each occurrence's own consensus decides: one
 * Native UPOS value scan over the occurrence forms keeps, per form, the value with the
 * highest conservative rating (rating - 2 rd). An occurrence whose text is all
 * White_Space, or whose best UPOS is not a content tag, is not an obligation.
 * An occurrence without a recorded UPOS standing stays required: absence is not false.
 */
typedef struct ObservedUpos
{
    hash128_t form;
    hash128_t upos;
    __int128 conservative;
} ObservedUpos;

static void
receive_observed_upos(const LaplaceClassificationRow *row, void *context)
{
    HTAB *best = context;
    ObservedUpos *entry;
    bool found;
    __int128 conservative;
    if (!(laplace_walk_edge_weight(row->rating, row->rd) > 0.0))
        return;
    conservative = (__int128) row->rating - 2 * (__int128) row->rd;
    entry = hash_search(best, &row->subject, HASH_ENTER, &found);
    if (!found || conservative > entry->conservative ||
        (conservative == entry->conservative &&
         memcmp(&row->value, &entry->upos, sizeof(hash128_t)) < 0))
    {
        entry->upos = row->value;
        entry->conservative = conservative;
    }
}

static Bitmapset *
program_observed_content_origins(const LaplacePromptInput *input,
                                 const Datum *context_values,
                                 const Datum *node_values,
                                 int prompt_origin_count,
                                 const Bitmapset *eligible)
{
    const uint32 *text_off = tier_tree_text_off_array(input->tree);
    const uint32 *text_len = tier_tree_text_len_array(input->tree);
    size_t text_bytes = 0;
    const uint8 *text = tier_tree_text(input->tree, &text_bytes);
    hash128_t *forms;
    int form_count = 0;
    Bitmapset *required = NULL;
    HASHCTL ctl = {0};
    HTAB *best;
    int member = -1;

    forms = palloc(sizeof(hash128_t) * Max(prompt_origin_count, 1));
    while ((member = bms_next_member(eligible, member)) >= 0)
        forms[form_count++] = datum_to_hash128(context_values[member]);
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(ObservedUpos);
    ctl.hcxt = CurrentMemoryContext;
    best = hash_create("observed occurrence parts of speech", Max(form_count, 8), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    if (form_count > 0)
    {
        ArrayType *subjects = hash128_array_from_ids(forms, form_count);
        laplace_classification_scan(subjects,"upos",receive_observed_upos,best);
        pfree(subjects);
    }

    member = -1;
    while ((member = bms_next_member(eligible, member)) >= 0)
    {
        int32 node = DatumGetInt32(node_values[member]);
        hash128_t form = datum_to_hash128(context_values[member]);
        ObservedUpos *upos;
        if (text && text_off && text_len &&
            (size_t) text_off[node] + text_len[node] <= text_bytes &&
            text_len[node] > 0 &&
            laplace_text_is_all_whitespace(text + text_off[node], text_len[node]))
            continue;
        upos = hash_search(best, &form, HASH_FIND, NULL);
        if (upos && !laplace_upos_is_content(&upos->upos))
            continue;
        required = bms_add_member(required, member);
    }
    hash_destroy(best);
    pfree(forms);
    return required;
}

LaplaceCognitionProgram *
laplace_cognition_program_create(const LaplacePromptInput *input,
                                 int prompt_origin_count,
                                 const LaplaceQueryChannel *initial_channels,
                                 int initial_channel_count,
                                 const LaplacePromptIntent *intent)
{
    MemoryContext parent = CurrentMemoryContext;
    MemoryContext owner;
    MemoryContext previous;
    LaplaceCognitionProgram *program;
    Datum *node_values = NULL;
    bool *node_nulls = NULL;
    int node_value_count = 0;
    Datum *context_values = NULL;
    bool *context_nulls = NULL;
    int context_value_count = 0;
    int operation_count = intent ? intent->relation_count : 0;
    const uint8_t *tiers;
    size_t tree_nodes;
    Bitmapset *eligible = NULL;
    Bitmapset *content_required = NULL;
    Bitmapset *compiled_required = NULL;

    if (!input || !input->tree || !input->context || !input->nodes ||
        prompt_origin_count < 0 || initial_channel_count < 0 ||
        (initial_channel_count > 0 && !initial_channels))
        ereport(ERROR, (errmsg("cognition program: invalid prompt program input")));
    tree_nodes = tier_tree_node_count(input->tree);
    tiers = tier_tree_tier_array(input->tree);
    deconstruct_array(input->nodes, INT4OID, 4, true, TYPALIGN_INT,
                      &node_values, &node_nulls, &node_value_count);
    deconstruct_array(input->context, BYTEAOID, -1, false, TYPALIGN_INT,
                      &context_values, &context_nulls, &context_value_count);
    if (node_value_count != prompt_origin_count ||
        context_value_count != prompt_origin_count)
        ereport(ERROR,
                (errmsg("cognition program: prompt occurrence projections disagree")));

    /* Eligible obligations are the occurrences at tier >= 2 in the canonical
     * observation tree. */
    for (int i = 0; i < prompt_origin_count; ++i)
    {
        int32 node;
        if (node_nulls[i] || context_nulls[i])
            ereport(ERROR,
                    (errmsg("cognition program: prompt occurrence projections contain NULL")));
        node = DatumGetInt32(node_values[i]);
        if (node < 0 || (size_t) node >= tree_nodes)
            ereport(ERROR,
                    (errmsg("cognition program: prompt node index is outside canonical tree")));
        if (tiers[node] >= 2)
            eligible = bms_add_member(eligible, i);
    }

    /* With no declared operation, obligations narrow to content occurrences:
     * from a supported parse, else from recorded Native UPOS value claims. */
    if (operation_count == 0)
        content_required = program_content_origins(intent, prompt_origin_count);
    if (operation_count == 0 && !content_required && eligible)
        content_required = program_observed_content_origins(
            input, context_values, node_values, prompt_origin_count, eligible);

    /* Declared operations add their operand occurrences as obligations. */
    for (int i = 0; i < operation_count; ++i)
    {
        const LaplacePromptRelationRead *operation = &intent->operations[i];
        int member = -1;
        while ((member = bms_next_member(operation->operand_origins, member)) >= 0)
            if (member >= prompt_origin_count)
                elog(ERROR, "cognition program: operand outside admitted prompt");
        compiled_required = bms_add_members(compiled_required, operation->operand_origins);
    }

    owner = AllocSetContextCreate(parent, "cognition completion program",
                                  ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(owner);
    program = palloc0(sizeof(*program));
    program->owner = owner;
    program->root = input->root;
    program->explicit_invocation = intent && intent->explicit_invocation;
    if (program->explicit_invocation) program->active_context = intent->active_context;
    program->disposition = LAPLACE_COGNITION_OPEN;
    /* Content occurrences when narrowed; otherwise every eligible occurrence
     * plus declared operands. A declared operation below widens this to every
     * occurrence and adds one obligation per distinct input identity. */
    program->required = operation_count == 0 && content_required
        ? bms_copy(content_required)
        : bms_add_members(bms_copy(eligible), compiled_required);
    program->semantic_origins = semantic_origin_index(
        owner, prompt_origin_count + initial_channel_count + 1);
    program->invocation_results = semantic_origin_index(owner, initial_channel_count + 1);

    if (compiled_required && operation_count > 0)
    {
        HASHCTL input_ctl = {0};
        HTAB *input_ids;
        input_ctl.keysize = sizeof(hash128_t);
        input_ctl.entrysize = sizeof(InvocationInputEntry);
        input_ctl.hcxt = owner;
        input_ids = hash_create("invocation input identities", 16, &input_ctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        program->operations = palloc0(sizeof(LaplacePromptRelationRead) * operation_count);
        for (int i = 0; i < operation_count; ++i)
        {
            const LaplacePromptRelationRead *source = &intent->operations[i];
            LaplacePromptRelationRead *target = &program->operations[i];
            *target = *source;
            target->operand_origins = bms_copy(source->operand_origins);
            target->inputs = palloc(sizeof(*target->inputs) * source->input_count);
            for (int j = 0; j < source->input_count; ++j)
            {
                bool found;
                InvocationInputEntry *entry;
                target->inputs[j] = source->inputs[j];
                target->inputs[j].origins = bms_copy(source->inputs[j].origins);
                entry = hash_search(input_ids, &source->inputs[j].id, HASH_ENTER, &found);
                if (!found)
                {
                    if (hash_get_num_entries(input_ids) > INT_MAX)
                        elog(ERROR, "cognition program: too many input identity obligations");
                    entry->ordinal = (int) hash_get_num_entries(input_ids) - 1;
                }
                target->inputs[j].requirement = entry->ordinal;
                program->invocation_required = bms_add_member(
                    program->invocation_required, entry->ordinal);
            }
        }
        hash_destroy(input_ids);
        program->operation_relation_count = operation_count;
        for (int i = 0; i < prompt_origin_count; ++i)
            program->required = bms_add_member(program->required, i);
    }

    if (!program->required && prompt_origin_count > 0)
    {
        /* An observation with no eligible occurrence still requires all of its
         * occurrences, so an empty program cannot complete vacuously. */
        for (int i = 0; i < prompt_origin_count; ++i)
            program->required = bms_add_member(program->required, i);
    }

    /* Semantic provenance is seeded from the required occurrences' ids and the
     * observation root, which carries their union. */
    for (int i = 0; i < prompt_origin_count; ++i)
    {
        bytea *value;
        hash128_t id;
        if (!bms_is_member(i, program->required))
            continue;
        value = DatumGetByteaPP(context_values[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
            ereport(ERROR,
                    (errmsg("cognition program: prompt identity must be 16 bytes")));
        memcpy(&id, VARDATA_ANY(value), sizeof(id));
        semantic_origin_add(program, &id, i);
    }
    {
        SemanticOriginEntry *root = semantic_origin_get(program, &input->root, true);
        root->origins = bms_add_members(root->origins, program->required);
    }

    /* COUPLE bindings seed provenance with their required origins. A binding
     * alone still cannot close an obligation: emit also requires semantic
     * support. */
    if (intent && intent->bindings)
    {
        HASH_SEQ_STATUS sequence;
        LaplacePromptIntentBinding *binding;
        hash_seq_init(&sequence, intent->bindings);
        while ((binding = hash_seq_search(&sequence)) != NULL)
        {
            Bitmapset *required = bms_intersect(binding->origins, program->required);
            if (!bms_is_empty(required))
            {
                SemanticOriginEntry *origin = semantic_origin_get(program, &binding->id, true);
                origin->origins = bms_add_members(origin->origins, required);
            }
            bms_free(required);
        }
    }

    /* Initial channels extend provenance anchor -> candidate along salient,
     * positively rated relations traversed outbound or symmetric; an inbound
     * asymmetric relation is not inverted. */
    for (int i = 0; i < initial_channel_count; ++i)
        record_semantic_channel(program, &initial_channels[i]);

    program_fingerprint(program, context_values, intent,
                        initial_channels, initial_channel_count);
    MemoryContextSwitchTo(previous);

    bms_free(compiled_required);
    bms_free(content_required);
    bms_free(eligible);
    if (node_value_count > 0)
    {
        pfree(node_values);
        pfree(node_nulls);
    }
    if (context_value_count > 0)
    {
        pfree(context_values);
        pfree(context_nulls);
    }
    return program;
}

void
laplace_cognition_program_note_semantic_channels(
    LaplaceCognitionProgram *program,
    const LaplaceQueryChannel *channels,
    int channel_count)
{
    MemoryContext previous;

    if (!program || !channels || channel_count <= 0 || program->complete)
        return;
    previous = MemoryContextSwitchTo(program->owner);
    for (int i = 0; i < channel_count; ++i)
        record_semantic_channel(program, &channels[i]);
    MemoryContextSwitchTo(previous);
}

void
laplace_cognition_program_note_route(LaplaceCognitionProgram *program)
{
    if (!program || program->complete)
        return;
    if (program->routing_rounds == INT_MAX)
        elog(ERROR, "cognition program: routing round count overflow");
    ++program->routing_rounds;
}

void
laplace_cognition_program_note_emit(LaplaceCognitionProgram *program,
                                    const hash128_t *selected,
                                    const Bitmapset *origins,
                                    bool semantic_support)
{
    MemoryContext previous;
    Bitmapset *semantic = NULL;
    Bitmapset *covered = NULL;
    int output_index;

    if (!program || !selected)
        return;
    previous = MemoryContextSwitchTo(program->owner);
    program_reserve_output(program);
    output_index = program->output_count++;
    program->outputs[output_index] = *selected;
    program->output_semantic_support[output_index] = semantic_support;
    program->output_origins[output_index] = bms_copy(origins);
    if (semantic_support)
    {
        if (program->semantic_output_count == INT_MAX)
            elog(ERROR, "cognition program: semantic output count overflow");
        ++program->semantic_output_count;
    }

    /* An emitted id satisfies obligations only where the caller's reported
     * origins intersect its typed semantic provenance. Emitting an answer to
     * every declared input satisfies all occurrence obligations. */
    if (semantic_support && origins && program->required)
    {
        SemanticOriginEntry *grounding = semantic_origin_get(program, selected, false);
        if (grounding && grounding->origins)
        {
            semantic = bms_intersect(origins, grounding->origins);
            covered = bms_intersect(semantic, program->required);
            program->satisfied = bms_add_members(program->satisfied, covered);
            bms_free(covered);
            bms_free(semantic);
        }
    }
    if (semantic_support && program->invocation_required)
    {
        SemanticOriginEntry *result = hash_search(program->invocation_results,
                                                  selected, HASH_FIND, NULL);
        if (result)
            program->invocation_satisfied = bms_add_members(
                program->invocation_satisfied, result->origins);
        if (bms_is_subset(program->invocation_required, program->invocation_satisfied))
            program->satisfied = bms_add_members(program->satisfied, program->required);
    }
    program_output_fingerprint(program);
    program_try_complete(program);
    MemoryContextSwitchTo(previous);
}

void
laplace_cognition_program_finalize(LaplaceCognitionProgram *program,
                                   LaplaceCognitionDisposition disposition)
{
    if (!program || program->complete)
        return;
    if (program->disposition == LAPLACE_COGNITION_AMBIGUOUS)
        return;
    if (disposition != LAPLACE_COGNITION_EXHAUSTED &&
        disposition != LAPLACE_COGNITION_BUDGET_EXHAUSTED &&
        disposition != LAPLACE_COGNITION_AMBIGUOUS)
        disposition = LAPLACE_COGNITION_EXHAUSTED;
    program->disposition = disposition;
}

const Bitmapset *
laplace_cognition_program_required(const LaplaceCognitionProgram *program)
{
    return program ? program->required : NULL;
}

const Bitmapset *
laplace_cognition_program_semantic_origins(const LaplaceCognitionProgram *program,
                                           const hash128_t *id)
{
    SemanticOriginEntry *entry;
    if (!program || !id || !program->semantic_origins)
        return NULL;
    entry = hash_search(program->semantic_origins, id, HASH_FIND, NULL);
    return entry ? entry->origins : NULL;
}

Bitmapset *
laplace_cognition_program_remaining(const LaplaceCognitionProgram *program)
{
    if (!program || !program->required)
        return NULL;
    return bms_difference(program->required, program->satisfied);
}

void
laplace_cognition_program_receipt(const LaplaceCognitionProgram *program,
                                  LaplaceCognitionProgramReceipt *receipt)
{
    int64 required;
    int64 satisfied;

    if (!receipt)
        return;
    MemSet(receipt, 0, sizeof(*receipt));
    if (!program)
        return;
    /* Occurrence obligations and declared input-identity obligations are
     * counted separately and summed. */
    required = (int64) bms_num_members(program->required) +
               bms_num_members(program->invocation_required);
    satisfied = (int64) bms_num_members(program->satisfied) +
                bms_num_members(program->invocation_satisfied);
    if (required > INT_MAX || satisfied > INT_MAX)
        elog(ERROR, "cognition program: receipt obligation count exceeds int capacity");
    receipt->program_id = program->program_id;
    receipt->output_fingerprint = program->output_fingerprint;
    receipt->semantic_act_id = program->semantic_act_id;
    receipt->required_obligations = required;
    receipt->satisfied_obligations = satisfied;
    receipt->remaining_required = Max(required - satisfied, 0);
    receipt->routing_rounds = program->routing_rounds;
    receipt->output_count = program->output_count;
    receipt->semantic_output_count = program->semantic_output_count;
    receipt->output_present = program->output_present;
    receipt->semantic_act_present = program->semantic_act_present;
    receipt->complete = program->complete;
    receipt->disposition = program->disposition;
}

const char *
laplace_cognition_disposition_name(LaplaceCognitionDisposition disposition)
{
    switch (disposition)
    {
        case LAPLACE_COGNITION_OPEN: return "open";
        case LAPLACE_COGNITION_COMPLETE: return "complete";
        case LAPLACE_COGNITION_EXHAUSTED: return "unresolved";
        case LAPLACE_COGNITION_BUDGET_EXHAUSTED: return "budget_exhausted";
        case LAPLACE_COGNITION_AMBIGUOUS: return "ambiguous";
        default: return "invalid";
    }
}

void
laplace_cognition_program_destroy(LaplaceCognitionProgram **program)
{
    MemoryContext owner;
    if (!program || !*program)
        return;
    owner = (*program)->owner;
    *program = NULL;
    MemoryContextDelete(owner);
}
