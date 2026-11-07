#include "postgres.h"

#include <limits.h>

#include "catalog/pg_type.h"
#include "lib/stringinfo.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "cognition_program_internal.h"
#include "spi_common.h"

static hash128_t
cognition_domain(const char *name)
{
    hash128_t id;
    hash128_blake3_str(name, &id);
    return id;
}

/* Program and output fingerprints encode integers big-endian so the program
 * id is a content identity independent of host layout. */
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

void
program_fingerprint(LaplaceCognitionProgram *program, Datum *context_values,
                    const LaplacePromptIntent *intent,
                    const LaplaceQueryChannel *initial_channels,
                    int initial_channel_count)
{
    StringInfoData bytes;
    /* Each response plane is encoded as its own section, so one canonical id
     * reached through the observation, discourse, structure, geometry and typed
     * channels stays a distinct program coordinate per plane. */
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

    /* The whole COUPLE response (bindings, structural trajectory crossings,
     * geometry neighbors, discourse, typed consensus channels) and any declared
     * operations are bound into the program id, each plane sorted canonically.
     * Structure and geometry are fingerprinted as response data only; they do
     * not enter semantic provenance. */
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

void
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

void
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
