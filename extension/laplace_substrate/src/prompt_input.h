#ifndef LAPLACE_PROMPT_INPUT_H
#define LAPLACE_PROMPT_INPUT_H

#include "postgres.h"
#include "utils/array.h"
#include "laplace/core/tier_tree.h"

/* The query owns its canonical tree for the complete native operation.
 * Arrays are indexed projections of that tree, not replacement query state. */
typedef struct LaplacePromptInput
{
    tier_tree_t *tree;
    hash128_t root;
    ArrayType *context;
    ArrayType *nodes;
    ArrayType *seeds;
    MemoryContextCallback cleanup;
} LaplacePromptInput;

LaplacePromptInput *laplace_prompt_input(text *input);
/* Ordered complete tree cut. Every byte remains covered, including leaves
 * below the requested altitude and repeated identity occurrences. */
ArrayType *laplace_prompt_input_cut(const LaplacePromptInput *input, uint8 tier);

#endif
