#include "postgres.h"

#include <limits.h>

#include "catalog/pg_type.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"

#include "laplace/core/hash128.h"

#include "cognition_program.h"
#include "walk_score.h"

typedef struct SemanticOriginEntry
{
    hash128_t id;
    Bitmapset *origins;
} SemanticOriginEntry;

struct LaplaceCognitionProgram
{
    MemoryContext owner;
    hash128_t root;
    hash128_t program_id;
    Bitmapset *required;
    Bitmapset *satisfied;
    HTAB *direct_semantic_origins;
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
    return hash_create("cognition direct semantic origins", Max(expected, 16), &ctl,
                       HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
}

static void
record_direct_semantic_origin(LaplaceCognitionProgram *program,
                              const LaplaceQueryChannel *channel,
                              Bitmapset *eligible, int prompt_origin_count)
{
    int origin = channel->ordinal - 1;
    bool found;
    SemanticOriginEntry *entry;

    if (origin < 0 || origin >= prompt_origin_count ||
        !bms_is_member(origin, eligible) ||
        !(walk_edge_score(channel->relation, channel->rating, channel->rd) > 0.0))
        return;

    entry = hash_search(program->direct_semantic_origins,
                        &channel->candidate, HASH_ENTER, &found);
    if (!found)
        entry->origins = NULL;
    entry->origins = bms_add_member(entry->origins, origin);
}

LaplaceCognitionProgram *
laplace_cognition_program_create(const LaplacePromptInput *input,
                                 int prompt_origin_count,
                                 const LaplaceQueryChannel *initial_channels,
                                 int initial_channel_count)
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
    const uint8_t *tiers;
    size_t tree_nodes;
    Bitmapset *eligible = NULL;
    hash128_t *required_ids;
    int required_count = 0;

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

    /* These are exact semantic-coordinate requirements, not evidence-presence
     * requirements. A coordinate with no currently addressable typed edge must
     * remain required and unresolved. Dropping it merely because proposal-time
     * evidence cannot reach it turns missing knowledge into false completion. */
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

    owner = AllocSetContextCreate(parent, "cognition completion program",
                                  ALLOCSET_DEFAULT_SIZES);
    previous = MemoryContextSwitchTo(owner);
    program = palloc0(sizeof(*program));
    program->owner = owner;
    program->root = input->root;
    program->disposition = LAPLACE_COGNITION_OPEN;
    program->required = bms_copy(eligible);
    program->direct_semantic_origins = semantic_origin_index(owner, initial_channel_count);

    /* Preserve semantic and structural provenance as different facts. The
     * forward executor's ancestry bitmap can contain both a trajectory suffix
     * and typed relation ancestry. It is therefore not, by itself, proof that
     * every inherited prompt coordinate received semantic support. Record the
     * direct typed grounding available at program compilation and require an
     * emitted identity to match that grounding before it may satisfy a semantic
     * coordinate. Later routed semantic grounding remains conservative here:
     * until it is explicitly supplied as a typed resolution it cannot create a
     * false completion certificate. */
    for (int i = 0; i < initial_channel_count; ++i)
        record_direct_semantic_origin(program, &initial_channels[i], eligible,
                                      prompt_origin_count);

    if (!program->required && prompt_origin_count > 0)
    {
        /* A punctuation-only/unknown prompt still has an exact observation.
         * Keep it as an unresolved requirement rather than auto-completing an
         * empty semantic program. */
        for (int i = 0; i < prompt_origin_count; ++i)
            program->required = bms_add_member(program->required, i);
    }

    required_count = bms_num_members(program->required);
    required_ids = palloc(sizeof(hash128_t) * Max(required_count, 1));
    {
        int member = -1;
        int at = 0;
        while ((member = bms_next_member(program->required, member)) >= 0)
        {
            bytea *value = DatumGetByteaPP(context_values[member]);
            if (VARSIZE_ANY_EXHDR(value) != sizeof(hash128_t))
                ereport(ERROR,
                        (errmsg("cognition program: prompt identity must be 16 bytes")));
            memcpy(&required_ids[at++], VARDATA_ANY(value), sizeof(hash128_t));
        }
    }
    fingerprint_id_sequence("laplace:cognition-program:v1", &program->root,
                            required_ids, required_count, &program->program_id);
    pfree(required_ids);
    MemoryContextSwitchTo(previous);

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
     * identity must have direct positive typed grounding for the exact prompt
     * origin as well as carry that origin in the executor's provenance. */
    if (semantic_support && origins && program->required)
    {
        SemanticOriginEntry *grounding = hash_search(
            program->direct_semantic_origins, selected, HASH_FIND, NULL);
        if (grounding && grounding->origins)
        {
            semantic = bms_intersect(origins, grounding->origins);
            covered = bms_intersect(semantic, program->required);
            program->satisfied = bms_add_members(program->satisfied, covered);
            bms_free(covered);
            bms_free(semantic);
        }
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
    if (disposition != LAPLACE_COGNITION_EXHAUSTED &&
        disposition != LAPLACE_COGNITION_BUDGET_EXHAUSTED)
        disposition = LAPLACE_COGNITION_EXHAUSTED;
    program->disposition = disposition;
}

void
laplace_cognition_program_receipt(const LaplaceCognitionProgram *program,
                                  LaplaceCognitionProgramReceipt *receipt)
{
    int required;
    int satisfied;

    if (!receipt)
        return;
    MemSet(receipt, 0, sizeof(*receipt));
    if (!program)
        return;
    required = bms_num_members(program->required);
    satisfied = bms_num_members(program->satisfied);
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
