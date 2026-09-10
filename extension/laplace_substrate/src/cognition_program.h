#ifndef LAPLACE_COGNITION_PROGRAM_H
#define LAPLACE_COGNITION_PROGRAM_H

#include "postgres.h"
#include "nodes/bitmapset.h"

#include "laplace/core/hash128.h"

#include "prompt_input.h"
#include "query_evidence.h"

typedef enum LaplaceCognitionDisposition
{
    LAPLACE_COGNITION_OPEN = 0,
    LAPLACE_COGNITION_COMPLETE = 1,
    LAPLACE_COGNITION_EXHAUSTED = 2,
    LAPLACE_COGNITION_BUDGET_EXHAUSTED = 3
} LaplaceCognitionDisposition;

typedef struct LaplaceCognitionProgramReceipt
{
    hash128_t program_id;
    hash128_t output_fingerprint;
    hash128_t semantic_act_id;
    int32 required_obligations;
    int32 satisfied_obligations;
    int32 remaining_required;
    int32 routing_rounds;
    int32 output_count;
    int32 semantic_output_count;
    bool output_present;
    bool semantic_act_present;
    bool complete;
    LaplaceCognitionDisposition disposition;
} LaplaceCognitionProgramReceipt;

typedef struct LaplaceCognitionProgram LaplaceCognitionProgram;

/* Compile the exact admitted prompt into a finite completion program. Prompt
 * occurrence ordinals are the obligation coordinates; supplemental/history
 * operands remain usable evidence but never become obligations for this turn.
 * There is no prompt-text/keyword classifier here. */
LaplaceCognitionProgram *laplace_cognition_program_create(
    const LaplacePromptInput *input,
    int prompt_origin_count,
    const LaplaceQueryChannel *initial_channels,
    int initial_channel_count);

/* ROUTE changes working knowledge but is not an output act. */
void laplace_cognition_program_note_route(LaplaceCognitionProgram *program);

/* Register an emitted semantic constituent after its working-state transition.
 * `origins` is the exact prompt/working occurrence ancestry calculated by the
 * active providers. Completion requires required-origin closure and at least
 * one semantically supported output; structural continuity alone cannot claim
 * completion of a semantic request. */
void laplace_cognition_program_note_emit(
    LaplaceCognitionProgram *program,
    const hash128_t *selected,
    const Bitmapset *origins,
    bool semantic_support);

/* Seal an incomplete program when the search frontier or declared budget ends.
 * A completed semantic act cannot be downgraded by a later finalization call. */
void laplace_cognition_program_finalize(
    LaplaceCognitionProgram *program,
    LaplaceCognitionDisposition disposition);

void laplace_cognition_program_receipt(
    const LaplaceCognitionProgram *program,
    LaplaceCognitionProgramReceipt *receipt);

const char *laplace_cognition_disposition_name(LaplaceCognitionDisposition disposition);
void laplace_cognition_program_destroy(LaplaceCognitionProgram **program);

#endif
