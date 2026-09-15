#ifndef LAPLACE_PROMPT_STRUCTURE_H
#define LAPLACE_PROMPT_STRUCTURE_H

#include "postgres.h"
#include "utils/memutils.h"
#include "laplace/core/ud_parse.h"
#include "observation_read.h"
#include "prompt_input.h"

typedef struct LaplacePromptParse
{
    hash128_t id, physicality;
    hash128_t schema_id;
    hash128_t *constituents;
    size_t constituent_count;
    laplace_ud_parse_t decoded;
    laplace_ud_parse_status_t decode_status;
    /* Exact current prompt ordinal for each source token, or -1. A complete
     * alignment consumes every non-whitespace current occurrence in order. */
    int *token_origins;
    bool aligned;
    bool positive_standing;
    bool supported;
    LaplaceObservation *witnesses;
    int witness_count;
    int witness_capacity;
    MemoryContextCallback cleanup;
} LaplacePromptParse;

typedef struct LaplacePromptStructure
{
    MemoryContext owner;
    hash128_t root;
    hash128_t *forms;
    int *origins;
    int form_count;
    LaplacePromptParse **parses;
    int count;
    int capacity;
    bool ambiguous;
    bool budget_exhausted;
} LaplacePromptStructure;

/* Decode complete admitted structural trajectories and bind source token
 * positions to the current observation. This is COUPLE evidence: a parse,
 * frame, mood or role never itself authorizes an operation. */
LaplacePromptStructure *laplace_prompt_structure_couple(
    const LaplacePromptInput *input, ArrayType *relation_types,
    int fanout, MemoryContext owner);

#endif
