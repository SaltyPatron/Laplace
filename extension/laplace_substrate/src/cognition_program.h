#ifndef LAPLACE_COGNITION_PROGRAM_H
#define LAPLACE_COGNITION_PROGRAM_H

#include "postgres.h"
#include "nodes/bitmapset.h"
#include "utils/array.h"

#include "laplace/core/hash128.h"

#include "prompt_input.h"
#include "query_evidence.h"

typedef enum LaplaceCognitionDisposition
{
    LAPLACE_COGNITION_OPEN = 0,
    LAPLACE_COGNITION_COMPLETE = 1,
    LAPLACE_COGNITION_EXHAUSTED = 2,
    LAPLACE_COGNITION_BUDGET_EXHAUSTED = 3,
    LAPLACE_COGNITION_AMBIGUOUS = 4
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
struct LaplacePromptIntent;

/* Compile one admitted observation into a finite completion program whose id
 * fingerprints the whole COUPLE response. Occurrence ordinals of the
 * observation are the obligation coordinates; the observation root carries
 * their union. Operations declared in the intent add exact input-identity
 * obligations. Channels passed here seed semantic provenance only; they never
 * add obligations. */
LaplaceCognitionProgram *laplace_cognition_program_create(
    const LaplacePromptInput *input,
    int prompt_origin_count,
    const LaplaceQueryChannel *initial_channels,
    int initial_channel_count,
    const struct LaplacePromptIntent *intent);

/* Extend semantic provenance along positively rated typed channels routed
 * after COUPLE. Structural and geometric reachability never enter this map. */
void laplace_cognition_program_note_semantic_channels(
    LaplaceCognitionProgram *program,
    const LaplaceQueryChannel *channels,
    int channel_count);

/* Count one ROUTE round; routing is not an output. */
void laplace_cognition_program_note_route(LaplaceCognitionProgram *program);

/* Record one REALIZE output with the caller's occurrence ancestry. The program
 * completes, minting a semantic act id over program id and output fingerprint,
 * once every obligation is satisfied and at least one output carries semantic
 * support. */
void laplace_cognition_program_note_emit(
    LaplaceCognitionProgram *program,
    const hash128_t *selected,
    const Bitmapset *origins,
    bool semantic_support);

/* Seal an incomplete program as exhausted, budget-exhausted or ambiguous.
 * A complete or ambiguous program keeps its disposition. */
void laplace_cognition_program_finalize(
    LaplaceCognitionProgram *program,
    LaplaceCognitionDisposition disposition);

/* The universal parts of speech that carry content (NOUN, PROPN, VERB, ADJ, ADV,
 * NUM, INTJ); every other tag is a function word or punctuation. */
bool laplace_upos_is_content(const hash128_t *upos);

/* The occurrences this turn must ground before it completes. */
const Bitmapset *laplace_cognition_program_required(const LaplaceCognitionProgram *program);

/* The prompt occurrences an identity is grounded in through typed semantic
 * transitions only (never geometry, glue or structural ancestry); NULL if none. */
const Bitmapset *laplace_cognition_program_semantic_origins(
    const LaplaceCognitionProgram *program, const hash128_t *id);

/* The obligations no emitted constituent has grounded yet, allocated in the
 * caller's memory context; NULL when none remain. */
Bitmapset *laplace_cognition_program_remaining(const LaplaceCognitionProgram *program);

void laplace_cognition_program_receipt(
    const LaplaceCognitionProgram *program,
    LaplaceCognitionProgramReceipt *receipt);

const char *laplace_cognition_disposition_name(LaplaceCognitionDisposition disposition);
void laplace_cognition_program_destroy(LaplaceCognitionProgram **program);

#endif
