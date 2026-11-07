#ifndef LAPLACE_SUBSTRATE_COGNITION_PROGRAM_INTERNAL_H
#define LAPLACE_SUBSTRATE_COGNITION_PROGRAM_INTERNAL_H

#include "postgres.h"
#include "nodes/bitmapset.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "laplace/core/hash128.h"
#include "cognition_program.h"
#include "prompt_intent.h"

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

void program_fingerprint(LaplaceCognitionProgram *program, Datum *context_values,
                         const LaplacePromptIntent *intent,
                         const LaplaceQueryChannel *initial_channels,
                         int initial_channel_count);
void program_output_fingerprint(LaplaceCognitionProgram *program);
void program_try_complete(LaplaceCognitionProgram *program);

#endif
