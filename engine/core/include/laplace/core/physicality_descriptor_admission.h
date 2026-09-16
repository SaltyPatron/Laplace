#pragma once

#include "laplace/core/physicality_descriptor.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct physicality_descriptor_vocabulary physicality_descriptor_vocabulary_t;
typedef struct physicality_descriptor_materialization physicality_descriptor_materialization_t;

/* Build the actual ordinary source entity for this generated derivation. The
 * caller registers this returned content ID under the exact canonical name and
 * deposits the returned declaration stage without reflecting it as raw input.
 * Outputs publish only on success. The peak includes the fixed text-work
 * reservation; source identity is never inferred from a caller's opaque hash. */
const char* physicality_descriptor_generated_source_name(void);
physicality_descriptor_status_t physicality_descriptor_generated_source_create(
    size_t maximum_bytes, hash128_t* out_source_id, intent_stage_t** out_stage,
    size_t* out_peak_bytes);

/* The mutable session projection has its own explicit derivation source.
 * Its frozen name is ordinary content under the same floor; tenant identity
 * and the prompt/response priors do not stand in for this native operation. */
const char* physicality_descriptor_session_source_name(void);
physicality_descriptor_status_t physicality_descriptor_session_source_create(
    size_t maximum_bytes, hash128_t* out_source_id, intent_stage_t** out_stage,
    size_t* out_peak_bytes);

/* Creates the copied vocabulary through the ordinary content owner against
 * the currently loaded floor. No fallback atoms, ids or coordinates. The
 * returned vocabulary owns an ordinary stage until take_stage transfers it. */
physicality_descriptor_status_t physicality_descriptor_vocabulary_create(
    const hash128_t* source_id, size_t maximum_bytes,
    physicality_descriptor_vocabulary_t** out_vocabulary);
void physicality_descriptor_vocabulary_free(physicality_descriptor_vocabulary_t* vocabulary);
const physicality_descriptor_basis_t* physicality_descriptor_vocabulary_basis(
    const physicality_descriptor_vocabulary_t* vocabulary);
const hash128_t* physicality_descriptor_vocabulary_floor_receipt(
    const physicality_descriptor_vocabulary_t* vocabulary);
intent_stage_t* physicality_descriptor_vocabulary_take_stage(
    physicality_descriptor_vocabulary_t* vocabulary);
size_t physicality_descriptor_vocabulary_bytes(const physicality_descriptor_vocabulary_t* vocabulary);
/* Initialization high-water reservation: observed retained capacity plus the
 * proven 64 KiB scratch upper bound for the frozen <=64-ASCII-codepoint content
 * literals and any newly allocated floor index. This is not process RSS. */
size_t physicality_descriptor_vocabulary_peak_bytes(const physicality_descriptor_vocabulary_t* vocabulary);
/* Floor-owned index allocation performed by this initialization; separately
 * retained by the floor after vocabulary_free. Already prepared means zero. */
size_t physicality_descriptor_vocabulary_floor_index_added_bytes(
    const physicality_descriptor_vocabulary_t* vocabulary);

/* One boundary for an entire managed/provider physicality batch. It delegates
 * each tuple's semantics to the existing native stage owner. On failure the
 * caller discards the stage; successful preceding tuples are not rolled back.
 * Raw-row callers pass their actual claimed placement IDs for validation;
 * NULL explicitly requests canonical address generation for new provider rows. */
physicality_descriptor_status_t physicality_descriptor_stage_add_batch(
    intent_stage_t* stage, const physicality_descriptor_input_t* inputs,
    const hash128_t* declared_physicality_ids,
    const int64_t* observed_at_unix_us, size_t count);

/* Scans exact tuple framing and stored carriers without expanding runs or
 * hashing their logical content. The logical grant/receipt counts Content RLE
 * hash operands; all typed/ordinary stored vertices are reported separately.
 * Rejects invalid ordinary counts and excess work before hashing. Typed factor
 * and testimony count fields retain their physicality owner's meaning.
 * Outputs are success-only. */
physicality_descriptor_status_t physicality_descriptor_stages_preflight(
    const intent_stage_t* const* stages, size_t stage_count,
    size_t maximum_logical_occurrences, size_t* out_body_count,
    size_t* out_stored_vertices, size_t* out_logical_occurrences);

/* NEEDS_PROVIDER publishes only a pending frontier, with no generated stage. */

typedef struct {
    hash128_t descriptor_id;
    hash128_t view_id;
} physicality_descriptor_admitted_form_t;

typedef struct {
    hash128_t source_id;
    /* Exact receipt identifier, not assumed to be an existing entity. Native
     * materialization creates a typed ordinary source+unit context entity. */
    hash128_t source_unit_id;
    /* Actual registered source prior supplied by the source owner. */
    double source_trust;
} physicality_descriptor_source_observation_t;

/* current_content_stages are actual Content bodies from one pinned reader
 * transaction. Explicitly missing ids distinguish a checked absence from an
 * unqueried reference. On absence, admitted_content_stages supply the same
 * original stages, in the same order, that the writer will install. Every
 * typed body is authenticated; the first Content placement per entity is the
 * provider winner, matching the writer's first-placement deduplication. Alternate
 * raw forms in captured_source do not choose a provider. Otherwise the frontier requests the next
 * bulk provider read. The finite closure contains original physicalities only;
 * generated descriptor/view rows are never fed back into that closure. */
physicality_descriptor_status_t physicality_descriptor_materialize(
    const physicality_descriptor_capture_t* captured_source,
    const physicality_descriptor_vocabulary_t* vocabulary,
    const intent_stage_t* const* current_content_stages, size_t current_stage_count,
    const intent_stage_t* const* admitted_content_stages, size_t admitted_stage_count,
    const hash128_t* explicitly_missing_ids, size_t missing_count,
    const physicality_descriptor_source_observation_t* observation_sources,
    size_t observation_source_count,
    const hash128_t* source_id, int64_t observed_at_unix_us,
    size_t maximum_bytes,
    physicality_descriptor_materialization_t** out_materialization);
void physicality_descriptor_materialization_free(
    physicality_descriptor_materialization_t* materialization);
const hash128_t* physicality_descriptor_materialization_pending(
    const physicality_descriptor_materialization_t* materialization, size_t* count);
const physicality_descriptor_admitted_form_t* physicality_descriptor_materialization_forms(
    const physicality_descriptor_materialization_t* materialization, size_t* count);
intent_stage_t* physicality_descriptor_materialization_take_stage(
    physicality_descriptor_materialization_t* materialization);
size_t physicality_descriptor_materialization_bytes(
    const physicality_descriptor_materialization_t* materialization);
size_t physicality_descriptor_materialization_peak_bytes(
    const physicality_descriptor_materialization_t* materialization);

#ifdef __cplusplus
}
#endif
