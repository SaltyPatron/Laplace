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

#include "cognition_program.h"
#include "consensus_scan.h"
#include "spi_common.h"
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

typedef struct ProgramBindingSnapshot
{
    hash128_t id;
    const Bitmapset *origins;
} ProgramBindingSnapshot;

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
    bool *output_semantic_support;
    Bitmapset **output_origins;
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

/* Fingerprint the source-attributed invocation, the complete pre-ORIENT
 * response field, and exact input occurrences. Integers use portable byte
 * order so receipts are content identities rather than host-layout artifacts. */
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
program_fingerprint_u64(StringInfo bytes, uint64 value)
{
    unsigned char encoded[8] = {
        (unsigned char) (value >> 56), (unsigned char) (value >> 48),
        (unsigned char) (value >> 40), (unsigned char) (value >> 32),
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
        program_fingerprint_u32(bytes, (uint32) member);
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
    order = memcmp(&a->call_witness, &b->call_witness, sizeof(hash128_t));
    if (order) return order;
    const hash128_t a_shape[] = {a->shape_id, a->exemplar_parse, a->current_parse,
        a->applicability_witness, a->parse_witness, a->exemplar_parse_witness};
    const hash128_t b_shape[] = {b->shape_id, b->exemplar_parse, b->current_parse,
        b->applicability_witness, b->parse_witness, b->exemplar_parse_witness};
    return memcmp(a_shape, b_shape, sizeof(a_shape));
}

static int
program_binding_compare(const void *left, const void *right)
{
    const ProgramBindingSnapshot *a = left, *b = right;
    return memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static int
program_structural_compare(const void *left, const void *right)
{
    const LaplaceStructuralCandidate *a = left, *b = right;
    int order = memcmp(&a->source, &b->source, sizeof(hash128_t));
    if (order) return order;
    order = memcmp(&a->id, &b->id, sizeof(hash128_t));
    if (order) return order;
    if (a->relation_mask != b->relation_mask)
        return a->relation_mask < b->relation_mask ? -1 : 1;
    if (a->occurrences != b->occurrences)
        return a->occurrences < b->occurrences ? -1 : 1;
    if (a->nearest_gap != b->nearest_gap)
        return a->nearest_gap < b->nearest_gap ? -1 : 1;
    return 0;
}

static int
program_geometry_compare(const void *left, const void *right)
{
    const LaplacePromptGeometryCandidate *a = left, *b = right;
    int order = memcmp(&a->source, &b->source, sizeof(hash128_t));
    if (order) return order;
    if (a->plane != b->plane) return a->plane < b->plane ? -1 : 1;
    if (a->rank != b->rank) return a->rank < b->rank ? -1 : 1;
    order = memcmp(&a->id, &b->id, sizeof(hash128_t));
    if (order) return order;
    if (a->plane == LAPLACE_PROMPT_GEOMETRY_HILBERT)
        return memcmp(a->hilbert_delta, b->hilbert_delta,
                      sizeof(a->hilbert_delta));
    if (a->distance < b->distance) return -1;
    if (a->distance > b->distance) return 1;
    return 0;
}

static int
program_channel_compare(const void *left, const void *right)
{
    const LaplaceQueryChannel *a = left, *b = right;
    int order;
    if (a->ordinal != b->ordinal) return a->ordinal < b->ordinal ? -1 : 1;
    if (a->operand_role != b->operand_role)
        return a->operand_role < b->operand_role ? -1 : 1;
    order = memcmp(&a->anchor, &b->anchor, sizeof(hash128_t));
    if (order) return order;
    order = memcmp(&a->candidate, &b->candidate, sizeof(hash128_t));
    if (order) return order;
    order = memcmp(&a->relation, &b->relation, sizeof(hash128_t));
    if (order) return order;
    if (a->outbound != b->outbound) return a->outbound ? 1 : -1;
#define PROGRAM_CHANNEL_CMP(field) \
    do { if (a->field != b->field) return a->field < b->field ? -1 : 1; } while (0)
    PROGRAM_CHANNEL_CMP(rating);
    PROGRAM_CHANNEL_CMP(rd);
    PROGRAM_CHANNEL_CMP(volatility);
    PROGRAM_CHANNEL_CMP(witnesses);
    PROGRAM_CHANNEL_CMP(confirm_occurrences);
    PROGRAM_CHANNEL_CMP(draw_occurrences);
    PROGRAM_CHANNEL_CMP(refute_occurrences);
    PROGRAM_CHANNEL_CMP(observation_occurrences);
    PROGRAM_CHANNEL_CMP(observation_rows);
    PROGRAM_CHANNEL_CMP(distinct_sources);
    PROGRAM_CHANNEL_CMP(distinct_contexts);
#undef PROGRAM_CHANNEL_CMP
    return memcmp(&a->provenance_root, &b->provenance_root, sizeof(hash128_t));
}

static void
program_fingerprint_bindings(StringInfo bytes, const LaplacePromptIntent *intent)
{
    long count = intent && intent->bindings ? hash_get_num_entries(intent->bindings) : 0;
    if (count < 0 || count > INT_MAX ||
        (Size) count > MaxAllocSize / sizeof(ProgramBindingSnapshot))
        elog(ERROR, "cognition program: binding response set exceeds allocation capacity");
    program_fingerprint_u32(bytes, (uint32) count);
    if (count == 0) return;

    ProgramBindingSnapshot *items = palloc(sizeof(*items) * (Size) count);
    HASH_SEQ_STATUS sequence;
    LaplacePromptIntentBinding *binding;
    long used = 0;
    hash_seq_init(&sequence, intent->bindings);
    while ((binding = hash_seq_search(&sequence)) != NULL)
    {
        items[used].id = binding->id;
        items[used].origins = binding->origins;
        ++used;
    }
    if (used != count)
        elog(ERROR, "cognition program: binding response set changed during fingerprint");
    qsort(items, (size_t) count, sizeof(*items), program_binding_compare);
    for (long i = 0; i < count; ++i)
    {
        appendBinaryStringInfo(bytes, (const char *) &items[i].id, sizeof(hash128_t));
        program_fingerprint_origins(bytes, items[i].origins);
    }
    pfree(items);
}

static void
program_fingerprint_structural(StringInfo bytes, const LaplacePromptIntent *intent)
{
    int count = intent ? intent->structural_count : 0;
    if (count < 0 || (count > 0 && !intent->structural) ||
        (Size) count > MaxAllocSize / sizeof(LaplaceStructuralCandidate))
        elog(ERROR, "cognition program: structural response set is invalid");
    program_fingerprint_u32(bytes, (uint32) count);
    if (count == 0) return;

    LaplaceStructuralCandidate *items = palloc(sizeof(*items) * (Size) count);
    memcpy(items, intent->structural, sizeof(*items) * (Size) count);
    qsort(items, (size_t) count, sizeof(*items), program_structural_compare);
    for (int i = 0; i < count; ++i)
    {
        appendBinaryStringInfo(bytes, (const char *) &items[i].source, sizeof(hash128_t));
        appendBinaryStringInfo(bytes, (const char *) &items[i].id, sizeof(hash128_t));
        program_fingerprint_u32(bytes, items[i].relation_mask);
        program_fingerprint_u64(bytes, (uint64) items[i].occurrences);
        program_fingerprint_u64(bytes, items[i].nearest_gap);
    }
    pfree(items);
}


static void
program_fingerprint_geometry(StringInfo bytes, const LaplacePromptIntent *intent)
{
    int count = intent ? intent->geometry_count : 0;
    if (count < 0 || (count > 0 && !intent->geometry) ||
        (Size) count > MaxAllocSize / sizeof(LaplacePromptGeometryCandidate))
        elog(ERROR, "cognition program: geometry response set is invalid");
    program_fingerprint_u32(bytes, (uint32) count);
    if (count == 0) return;

    LaplacePromptGeometryCandidate *items =
        palloc(sizeof(*items) * (Size) count);
    memcpy(items, intent->geometry, sizeof(*items) * (Size) count);
    qsort(items, (size_t) count, sizeof(*items), program_geometry_compare);
    for (int i = 0; i < count; ++i)
    {
        const LaplacePromptGeometryCandidate *candidate = &items[i];
        appendBinaryStringInfo(bytes, (const char *) &candidate->source,
                               sizeof(hash128_t));
        appendBinaryStringInfo(bytes, (const char *) &candidate->id,
                               sizeof(hash128_t));
        program_fingerprint_u32(bytes, candidate->plane);
        program_fingerprint_u32(bytes, candidate->rank);
        if (candidate->plane == LAPLACE_PROMPT_GEOMETRY_HILBERT)
            appendBinaryStringInfo(bytes,
                (const char *) candidate->hilbert_delta,
                sizeof(candidate->hilbert_delta));
        else
        {
            uint64 bits = 0;
            memcpy(&bits, &candidate->distance, sizeof(bits));
            program_fingerprint_u64(bytes, bits);
        }
    }
    pfree(items);
}

static void
program_fingerprint_discourse(StringInfo bytes, const LaplacePromptIntent *intent)
{
    ArrayType *discourse = intent ? intent->discourse : NULL;
    Datum *values = NULL;
    bool *nulls = NULL;
    int count = 0;

    if (!discourse)
    {
        program_fingerprint_u32(bytes, 0);
        return;
    }
    if (ARR_NDIM(discourse) > 1 || ARR_ELEMTYPE(discourse) != BYTEAOID)
        elog(ERROR, "cognition program: discourse response plane is invalid");
    deconstruct_array(discourse, BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &count);
    program_fingerprint_u32(bytes, (uint32) count);
    for (int i = 0; i < count; ++i)
    {
        program_fingerprint_u32(bytes, nulls[i] ? 0u : 1u);
        if (!nulls[i])
        {
            hash128_t id = datum_to_hash128(values[i]);
            appendBinaryStringInfo(bytes, (const char *) &id, sizeof(id));
        }
    }
    if (count > 0)
    {
        pfree(values);
        pfree(nulls);
    }
}

static void
program_fingerprint_channels(StringInfo bytes,
                             const LaplaceQueryChannel *channels, int count)
{
    if (count < 0 || (count > 0 && !channels) ||
        (Size) count > MaxAllocSize / sizeof(LaplaceQueryChannel))
        elog(ERROR, "cognition program: semantic response set is invalid");
    program_fingerprint_u32(bytes, (uint32) count);
    if (count == 0) return;

    LaplaceQueryChannel *items = palloc(sizeof(*items) * (Size) count);
    memcpy(items, channels, sizeof(*items) * (Size) count);
    qsort(items, (size_t) count, sizeof(*items), program_channel_compare);
    for (int i = 0; i < count; ++i)
    {
        const LaplaceQueryChannel *channel = &items[i];
        program_fingerprint_u32(bytes, (uint32) channel->ordinal);
        program_fingerprint_u32(bytes, channel->operand_role);
        appendBinaryStringInfo(bytes, (const char *) &channel->anchor, sizeof(hash128_t));
        appendBinaryStringInfo(bytes, (const char *) &channel->candidate, sizeof(hash128_t));
        appendBinaryStringInfo(bytes, (const char *) &channel->relation, sizeof(hash128_t));
        program_fingerprint_u32(bytes, channel->outbound ? 1u : 0u);
        program_fingerprint_u64(bytes, (uint64) channel->rating);
        program_fingerprint_u64(bytes, (uint64) channel->rd);
        program_fingerprint_u64(bytes, (uint64) channel->volatility);
        program_fingerprint_u64(bytes, (uint64) channel->witnesses);
        program_fingerprint_u64(bytes, (uint64) channel->confirm_occurrences);
        program_fingerprint_u64(bytes, (uint64) channel->draw_occurrences);
        program_fingerprint_u64(bytes, (uint64) channel->refute_occurrences);
        program_fingerprint_u64(bytes, (uint64) channel->observation_occurrences);
        program_fingerprint_u32(bytes, (uint32) channel->observation_rows);
        program_fingerprint_u32(bytes, (uint32) channel->distinct_sources);
        program_fingerprint_u32(bytes, (uint32) channel->distinct_contexts);
        appendBinaryStringInfo(bytes, (const char *) &channel->provenance_root,
                               sizeof(channel->provenance_root));
    }
    pfree(items);
}

static void
program_fingerprint(LaplaceCognitionProgram *program, Datum *context_values,
                    const LaplacePromptIntent *intent,
                    const LaplaceQueryChannel *initial_channels,
                    int initial_channel_count)
{
    StringInfoData bytes;
    /* v9 additionally binds the deterministic geometry response plane.
     * Equal canonical ids reached through current observation, prior discourse,
     * physicality, geometry, and generated working state remain distinct program
     * coordinates instead of collapsing into one semantic bag. */
    hash128_t domain = cognition_domain("laplace:cognition-program:v9");
    int member = -1;
    initStringInfo(&bytes);
    appendBinaryStringInfo(&bytes, (const char *) &domain, sizeof(domain));
    appendBinaryStringInfo(&bytes, (const char *) &program->root, sizeof(program->root));
    program_fingerprint_u32(&bytes, program->explicit_invocation ? 1u : 0u);
    if (program->explicit_invocation)
        appendBinaryStringInfo(&bytes, (const char *) &program->active_context, sizeof(hash128_t));
    program_fingerprint_u32(&bytes, (uint32) bms_num_members(program->required));
    while ((member = bms_next_member(program->required, member)) >= 0)
    {
        hash128_t id = datum_to_hash128(context_values[member]);
        program_fingerprint_u32(&bytes, (uint32) member);
        appendBinaryStringInfo(&bytes, (const char *) &id, sizeof(id));
    }

    /* COUPLE is part of the executable program state, not disposable setup.
     * Bind every exact prompt-relative identity route, native physicality crossing,
     * deterministic geometry response and retained typed semantic response into
     * the program id before ORIENT/ROUTE output can be claimed. Physicality and
     * geometry remain typed response data; neither becomes semantic testimony. */
    program_fingerprint_bindings(&bytes, intent);
    program_fingerprint_structural(&bytes, intent);
    program_fingerprint_geometry(&bytes, intent);
    program_fingerprint_discourse(&bytes, intent);
    program_fingerprint_channels(&bytes, initial_channels, initial_channel_count);

    program_fingerprint_u32(&bytes, (uint32) program->operation_relation_count);
    if (program->operation_relation_count > 0)
    {
        if ((Size) program->operation_relation_count >
            MaxAllocSize / sizeof(LaplacePromptRelationRead))
            elog(ERROR, "cognition program: operation response set exceeds allocation capacity");
        LaplacePromptRelationRead *ordered = palloc(
            sizeof(*ordered) * (Size) program->operation_relation_count);
        memcpy(ordered, program->operations,
               sizeof(*ordered) * (Size) program->operation_relation_count);
        qsort(ordered, (size_t) program->operation_relation_count,
              sizeof(*ordered), program_operation_compare);
        for (int i = 0; i < program->operation_relation_count; ++i)
        {
            const LaplacePromptRelationRead *operation = &ordered[i];
            appendBinaryStringInfo(&bytes, (const char *) &operation->result_relation,
                                   sizeof(operation->result_relation));
            appendBinaryStringInfo(&bytes, (const char *) &operation->source, sizeof(hash128_t));
            appendBinaryStringInfo(&bytes, (const char *) &operation->context, sizeof(hash128_t));
            appendBinaryStringInfo(&bytes, (const char *) &operation->call_witness, sizeof(hash128_t));
            const hash128_t shape_proof[] = {
                operation->shape_id, operation->exemplar_parse, operation->current_parse,
                operation->applicability_witness, operation->parse_witness,
                operation->exemplar_parse_witness};
            appendBinaryStringInfo(&bytes, (const char *) shape_proof, sizeof(shape_proof));
            program_fingerprint_u32(&bytes, (uint32) operation->input_count);
            for (int j = 0; j < operation->input_count; ++j)
            {
                const LaplacePromptOperand *input = &operation->inputs[j];
                appendBinaryStringInfo(&bytes, (const char *) &input->id, sizeof(hash128_t));
                appendBinaryStringInfo(&bytes, (const char *) &input->witness, sizeof(hash128_t));
                program_fingerprint_origins(&bytes, input->origins);
            }
            program_fingerprint_origins(&bytes, operation->operand_origins);
        }
        pfree(ordered);
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

    StringInfoData bytes;
    hash128_t domain = cognition_domain("laplace:cognition-output:v2");
    initStringInfo(&bytes);
    appendBinaryStringInfo(&bytes, (const char *) &domain, sizeof(domain));
    appendBinaryStringInfo(&bytes, (const char *) &program->root, sizeof(program->root));
    program_fingerprint_u32(&bytes, (uint32) program->output_count);
    for (int i = 0; i < program->output_count; ++i)
    {
        appendBinaryStringInfo(&bytes, (const char *) &program->outputs[i], sizeof(hash128_t));
        program_fingerprint_u32(&bytes, program->output_semantic_support[i] ? 1u : 0u);
        program_fingerprint_origins(&bytes, program->output_origins[i]);
    }
    hash128_blake3((const uint8_t *) bytes.data, bytes.len,
                   &program->output_fingerprint);
    pfree(bytes.data);
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

    /* A relation reached from a geometry responder remains a geometry-routed
     * semantic observation. It is fingerprinted in the program, but proximity
     * cannot satisfy prompt semantic obligations or mint semantic ancestry. */
    if (channel->operand_role == LAPLACE_QUERY_OPERAND_GEOMETRY)
        return;

    if (!(laplace_walk_edge_weight(channel->rating, channel->rd) > 0.0) ||
        !walk_relation_salient(channel->relation) ||
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

/*
 * Default conversation obligations are semantic-content coordinates, not every
 * surface/function-word occurrence. The whole prompt/parse/binding field still
 * participates in COUPLE/ORIENT and in the program fingerprint; this function
 * only says which current occurrences an ordinary semantic act must actually
 * ground before completion.
 *
 * Source-declared task/invocation contracts do not use this reduction: their
 * explicit operand obligations retain the existing stricter whole-prompt law.
 * With no supported aligned parse, return NULL and preserve the conservative
 * all-semantic-occurrence requirement.
 */
static bool
program_content_upos(const hash128_t *upos)
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
            if (program_content_upos(&parse->decoded.tokens[t].upos_id))
                required = bms_add_member(required, origin);
        }
    }
    return supported && required ? required : NULL;
}

/*
 * With no supported parse of the whole observation, each occurrence's own
 * evidence still separates content coordinates from structure. An occurrence
 * whose text is all White_Space, or whose strongest witnessed universal part of
 * speech is a function tag (the complement of the content set above), grounds
 * nothing by itself. An occurrence with no such evidence stays required:
 * missing evidence never erases an obligation.
 */
typedef struct ObservedUpos
{
    hash128_t form;
    hash128_t upos;
    __int128 conservative;
} ObservedUpos;

static void
receive_observed_upos(const LaplaceConsensusRow *row, void *context)
{
    HTAB *best = context;
    ObservedUpos *entry;
    bool found;
    __int128 conservative;
    if (row->object_is_null || !(laplace_walk_edge_weight(row->rating, row->rd) > 0.0))
        return;
    conservative = (__int128) row->rating - 2 * (__int128) row->rd;
    entry = hash_search(best, &row->subject, HASH_ENTER, &found);
    if (!found || conservative > entry->conservative ||
        (conservative == entry->conservative &&
         memcmp(&row->object, &entry->upos, sizeof(hash128_t)) < 0))
    {
        entry->upos = row->object;
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
    hash128_t has_pos;
    hash128_t *forms;
    int form_count = 0;
    Bitmapset *required = NULL;
    HASHCTL ctl = {0};
    HTAB *best;
    int member = -1;

    if (laplace_relation_type_id("HAS_POS", &has_pos) != 0)
        return NULL;
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
        ArrayType *types = hash128_array_from_ids(&has_pos, 1);
        laplace_consensus_scan(subjects, NULL, types, receive_observed_upos, best, NULL);
        pfree(subjects);
        pfree(types);
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
        if (upos && !program_content_upos(&upos->upos))
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

    /* For ordinary, uncontracted conversation, grammar distinguishes the
     * semantic content coordinates from determiners/auxiliaries/punctuation.
     * Every parse and binding still shapes the program; only obligation
     * closure is narrowed. Declared operations retain their exact contract. */
    if (operation_count == 0)
        content_required = program_content_origins(intent, prompt_origin_count);
    if (operation_count == 0 && !content_required && eligible)
        content_required = program_observed_content_origins(
            input, context_values, node_values, prompt_origin_count, eligible);

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
    /* A declared task keeps the existing whole-prompt + exact-input closure.
     * Ordinary conversation instead requires the supported parse's semantic
     * content occurrences. The exact whole observation still controls coupling,
     * orientation and the program fingerprint. */
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

    /* Retain every prompt-relative route established by COUPLE. Structural
     * crossings have already populated these bindings with exact source
     * occurrence ancestry; importing the binding does not turn that route into
     * testimony or let it close a semantic obligation by itself. */
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

const Bitmapset *
laplace_cognition_program_required(const LaplaceCognitionProgram *program)
{
    return program ? program->required : NULL;
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
