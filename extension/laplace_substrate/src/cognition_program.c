#include "postgres.h"

#include <limits.h>

#include "catalog/pg_type.h"
#include "lib/stringinfo.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"

#include "cognition_program.h"
#include "prompt_intent.h"
#include "walk_score.h"

typedef struct InvocationInputEntry
{
    hash128_t id;
    int ordinal;
} InvocationInputEntry;

typedef struct SemanticOriginEntry
{
    hash128_t id;
    Bitmapset *origins;
} SemanticOriginEntry;

struct LaplaceCognitionProgram
{
    MemoryContext owner;
    hash128_t root;
    hash128_t active_context;
    bool explicit_invocation;
    hash128_t program_id;
    Bitmapset *required;
    Bitmapset *satisfied;
    LaplacePromptRelationRead *operations;
    int operation_relation_count;
    HTAB *semantic_origins;
    HTAB *invocation_results;
    Bitmapset *invocation_required;
    Bitmapset *invocation_satisfied;
    hash128_t *outputs;
    int output_count;
    int output_capacity;
    int semantic_output_count;
    int routing_rounds;
    hash128_t output_fingerprint;
    hash128_t semantic_act_id;
    bool output_present;
    bool semantic_act_present;
    bool complete;
    LaplaceCognitionDisposition disposition;
};

static hash128_t
cognition_domain(const char *name)
{
    hash128_t id;
    hash128_blake3_str(name, &id);
    return id;
}

static void
fingerprint_id_sequence(const char *domain, const hash128_t *root,
                        const hash128_t *ids, int count, hash128_t *out)
{
    hash128_t prefix = cognition_domain(domain);
    hash128_t *parts;
    int total;

    if (!root || !out || count < 0)
        elog(ERROR, "cognition program: invalid fingerprint request");
    if (count > INT_MAX - 2 ||
        (Size) (count + 2) > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "cognition program: fingerprint input exceeds allocation capacity");

    total = count + 2;
    parts = palloc(sizeof(hash128_t) * total);
    parts[0] = prefix;
    parts[1] = *root;
    if (count > 0)
        memcpy(parts + 2, ids, sizeof(hash128_t) * count);
    hash128_blake3((const uint8_t *) parts,
                   sizeof(hash128_t) * (size_t) total, out);
    pfree(parts);
}

/* Fingerprint the source-attributed invocation and exact input occurrences,
 * including observation identities. Integers have a portable byte order. */
static void
program_fingerprint_u32(StringInfo bytes, uint32 value)
{
    unsigned char encoded[4] = {
        (unsigned char) (value >> 24), (unsigned char) (value >> 16),
        (unsigned char) (value >> 8), (unsigned char) value
    };
    appendBinaryStringInfo(bytes, (const char *) encoded, sizeof(encoded));
}

static void
program_fingerprint_origins(StringInfo bytes, const Bitmapset *origins)
{
    int member = -1;
    program_fingerprint_u32(bytes, bms_num_members(origins));
    while ((member = bms_next_member(origins, member)) >= 0)
        program_fingerprint_u32(bytes, member);
}

static int
program_operation_compare(const void *left, const void *right)
{
    const LaplacePromptRelationRead *a = left, *b = right;
    int order = memcmp(&a->result_relation, &b->result_relation, sizeof(hash128_t));
    if (order) return order;
    order = memcmp(&a->source, &b->source, sizeof(hash128_t));
    if (order) return order;
    order = memcmp(&a->context, &b->context, sizeof(hash128_t));
    if (order) return order;
    return memcmp(&a->call_witness, &b->call_witness, sizeof(hash128_t));
}

static void
program_fingerprint(LaplaceCognitionProgram *program, Datum *context_values)
{
    StringInfoData bytes;
    hash128_t domain = cognition_domain("laplace:cognition-program:v3");
    int member = -1;
    initStringInfo(&bytes);
    appendBinaryStringInfo(&bytes, (const char *) &domain, sizeof(domain));
    appendBinaryStringInfo(&bytes, (const char *) &program->root, sizeof(program->root));
    program_fingerprint_u32(&bytes, program->explicit_invocation ? 1 : 0);
    if (program->explicit_invocation)
        appendBinaryStringInfo(&bytes, (const char *) &program->active_context, sizeof(hash128_t));
    program_fingerprint_u32(&bytes, bms_num_members(program->required));
    while ((member = bms_next_member(program->required, member)) >= 0)
    {
        hash128_t id = datum_to_hash128(context_values[member]);
        program_fingerprint_u32(&bytes, member);
        appendBinaryStringInfo(&bytes, (const char *) &id, sizeof(id));
    }
    if (program->operation_relation_count > 1)
        qsort(program->operations, program->operation_relation_count,
              sizeof(LaplacePromptRelationRead), program_operation_compare);
    program_fingerprint_u32(&bytes, program->operation_relation_count);
    for (int i = 0; i < program->operation_relation_count; ++i)
    {
        const LaplacePromptRelationRead *operation = &program->operations[i];
        appendBinaryStringInfo(&bytes, (const char *) &operation->result_relation,
                               sizeof(operation->result_relation));
        appendBinaryStringInfo(&bytes, (const char *) &operation->source, sizeof(hash128_t));
        appendBinaryStringInfo(&bytes, (const char *) &operation->context, sizeof(hash128_t));
        appendBinaryStringInfo(&bytes, (const char *) &operation->call_witness, sizeof(hash128_t));
        program_fingerprint_u32(&bytes, operation->input_count);
        for (int j = 0; j < operation->input_count; ++j)
        {
            const LaplacePromptOperand *input = &operation->inputs[j];
            appendBinaryStringInfo(&bytes, (const char *) &input->id, sizeof(hash128_t));
            appendBinaryStringInfo(&bytes, (const char *) &input->witness, sizeof(hash128_t));
            program_fingerprint_origins(&bytes, input->origins);
        }
        program_fingerprint_origins(&bytes, operation->operand_origins);
    }
    hash128_blake3((const uint8_t *) bytes.data, bytes.len, &program->program_id);
    pfree(bytes.data);
}

static void
program_output_fingerprint(LaplaceCognitionProgram *program)
{
    if (program->output_count <= 0)
    {
        hash128_zero(&program->output_fingerprint);
        program->output_present = false;
        return;
    }
    fingerprint_id_sequence("laplace:cognition-output:v1", &program->root,
                            program->outputs, program->output_count,
                            &program->output_fingerprint);
    program->output_present = true;
}

static void
program_try_complete(LaplaceCognitionProgram *program)
{
    hash128_t parts[3];

    if (program->complete || program->semantic_output_count <= 0)
        return;
    if (program->invocation_required &&
        !bms_is_subset(program->invocation_required, program->invocation_satisfied))
        return;
    if (program->required && !bms_is_subset(program->required, program->satisfied))
        return;

    parts[0] = cognition_domain("laplace:semantic-act:v1");
    parts[1] = program->program_id;
    parts[2] = program->output_fingerprint;
    hash128_blake3((const uint8_t *) parts, sizeof(parts),
                   &program->semantic_act_id);
    program->semantic_act_present = true;
    program->complete = true;
    program->disposition = LAPLACE_COGNITION_COMPLETE;
}

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
    if ((Size) capacity > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "cognition program: output set exceeds allocation capacity");
    program->outputs = program->outputs
        ? repalloc(program->outputs, sizeof(hash128_t) * capacity)
        : palloc(sizeof(hash128_t) * capacity);
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

static bool
semantic_channel_traversable(const LaplaceQueryChannel *channel)
{
    const laplace_relation_def_t *def = NULL;

    if (channel->outbound)
        return true;
    return laplace_relation_lookup(&channel->relation, &def) == 0 &&
           def != NULL && def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC;
}

static void
record_semantic_channel(LaplaceCognitionProgram *program,
                        const LaplaceQueryChannel *channel)
{
    SemanticOriginEntry *anchor;
    SemanticOriginEntry *candidate;

    if (!(laplace_walk_edge_weight(channel->rating, channel->rd) > 0.0) ||
        !semantic_channel_traversable(channel) ||
        laplace_prompt_contract_relation(&channel->relation))
        return;

    anchor = semantic_origin_get(program, &channel->anchor, false);
    if (!anchor || !anchor->origins)
        return;
    candidate = semantic_origin_get(program, &channel->candidate, true);
    candidate->origins = bms_add_members(candidate->origins, anchor->origins);

    /* The exact declared input, not inherited lexical/semantic ancestry,
     * establishes that this candidate satisfies an invocation input. Keep that
     * proof separate until the candidate is actually selected. */
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
    }
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

    /* Default unresolved program: every semantic coordinate remains required.
     * Missing evidence cannot silently erase an obligation. */
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

    /* An explicit whole-root contract binds exact input occurrences. Lexical
     * naming paths never assign request roles or erase other obligations. */
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
    /* The whole-root obligation closes only after every declared input has an
     * actual selected result from the called operation. */
    program->required = bms_add_members(bms_copy(eligible), compiled_required);
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
        /* A punctuation-only/unknown prompt still has an exact observation.
         * Keep it as an unresolved requirement rather than auto-completing an
         * empty semantic program. */
        for (int i = 0; i < prompt_origin_count; ++i)
            program->required = bms_add_member(program->required, i);
    }

    /* Semantic provenance starts at the admitted required prompt occurrences.
     * Supplemental history/frontier ids remain usable guidance but never become
     * completion obligations for this turn. */
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

    /* Retain the witnessed naming/sense routes by which prompt operands reached
     * their active identities. This includes reverse naming access, without
     * granting reverse traversal to an asymmetric result operation. */
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

    /* Proposal channels are not completion by themselves, but they establish
     * typed semantic reachability from the exact prompt/trunk. Incoming
     * asymmetric testimony is retained elsewhere as evidence and cannot be
     * inverted into a semantic transition here. */
    for (int i = 0; i < initial_channel_count; ++i)
        record_semantic_channel(program, &initial_channels[i]);

    program_fingerprint(program, context_values);
    MemoryContextSwitchTo(previous);

    bms_free(compiled_required);
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

    if (!program || !selected)
        return;
    previous = MemoryContextSwitchTo(program->owner);
    program_reserve_output(program);
    program->outputs[program->output_count++] = *selected;
    if (semantic_support)
    {
        if (program->semantic_output_count == INT_MAX)
            elog(ERROR, "cognition program: semantic output count overflow");
        ++program->semantic_output_count;
    }

    /* Structural ancestry cannot satisfy a semantic requirement. The selected
     * identity must have positive typed reachability from the same prompt
     * coordinates that the executor reports in its provenance. */
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
    /* Surface occurrences and declared semantic input identities are different
     * obligations. Several inputs may share one surface occurrence; closing its
     * ancestry must not hide the still-unanswered input identities in receipts. */
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
