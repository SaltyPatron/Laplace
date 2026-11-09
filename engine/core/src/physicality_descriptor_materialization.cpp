#include "physicality_descriptor_admission_internal.hpp"

#include "laplace/core/codepoint_table.h"

extern "C" physicality_descriptor_status_t physicality_descriptor_materialize(
    const physicality_descriptor_capture_t* captured_source,
    const physicality_descriptor_vocabulary_t* vocabulary,
    const intent_stage_t* const* current_content_stages, size_t current_stage_count,
    const intent_stage_t* const* admitted_content_stages, size_t admitted_stage_count,
    const hash128_t* explicitly_missing_ids, size_t missing_count,
    const physicality_descriptor_source_observation_t* observation_sources, size_t observation_source_count,
    int64_t observed_at_unix_us, size_t maximum_bytes,
    physicality_descriptor_materialization_t** out_materialization) {
    return physicality_descriptor_materialize_cancelable(captured_source, vocabulary,
        current_content_stages, current_stage_count, admitted_content_stages, admitted_stage_count,
        explicitly_missing_ids, missing_count, observation_sources, observation_source_count,
        observed_at_unix_us, maximum_bytes, nullptr, out_materialization);
}

extern "C" physicality_descriptor_status_t physicality_descriptor_materialize_cancelable(
    const physicality_descriptor_capture_t* captured_source,
    const physicality_descriptor_vocabulary_t* vocabulary,
    const intent_stage_t* const* current_content_stages, size_t current_stage_count,
    const intent_stage_t* const* admitted_content_stages, size_t admitted_stage_count,
    const hash128_t* explicitly_missing_ids, size_t missing_count,
    const physicality_descriptor_source_observation_t* observation_sources, size_t observation_source_count,
    int64_t observed_at_unix_us, size_t maximum_bytes,
    const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_materialization_t** out_materialization) {
    return physicality_descriptor_materialize_diagnosed_cancelable(captured_source, vocabulary,
        current_content_stages, current_stage_count, admitted_content_stages, admitted_stage_count,
        explicitly_missing_ids, missing_count, observation_sources, observation_source_count,
        observed_at_unix_us, maximum_bytes, cancellation, nullptr, out_materialization);
}

extern "C" physicality_descriptor_status_t physicality_descriptor_materialize_diagnosed_cancelable(
    const physicality_descriptor_capture_t* captured_source,
    const physicality_descriptor_vocabulary_t* vocabulary,
    const intent_stage_t* const* current_content_stages, size_t current_stage_count,
    const intent_stage_t* const* admitted_content_stages, size_t admitted_stage_count,
    const hash128_t* explicitly_missing_ids, size_t missing_count,
    const physicality_descriptor_source_observation_t* observation_sources, size_t observation_source_count,
    int64_t observed_at_unix_us, size_t maximum_bytes,
    const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_materialization_diagnostics_t* diagnostics,
    physicality_descriptor_materialization_t** out_materialization) {
    if (diagnostics != nullptr) {
        *diagnostics = {};
        diagnostics->materialization_grant_bytes = maximum_bytes;
    }
    const auto finish = [&](physicality_descriptor_status_t status) {
        if (diagnostics != nullptr) {
            diagnostics->status = status;
            if (status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED &&
                diagnostics->refusal_kind == PHYSICALITY_MATERIALIZATION_REFUSAL_NONE)
                diagnostics->refusal_kind = PHYSICALITY_MATERIALIZATION_REFUSAL_SIZE_OR_UNREPORTED;
        }
        return status;
    };
    if (out_materialization == nullptr) return finish(PHYSICALITY_DESCRIPTOR_INVALID);
    *out_materialization = nullptr;
    if (physicality_descriptor_cancel_requested(cancellation)) return finish(PHYSICALITY_DESCRIPTOR_CANCELLED);
    if (captured_source == nullptr || vocabulary == nullptr ||
        (current_stage_count != 0 && current_content_stages == nullptr) ||
        (admitted_stage_count != 0 && admitted_content_stages == nullptr) ||
        (missing_count != 0 && explicitly_missing_ids == nullptr) ||
        (observation_source_count != 0 && observation_sources == nullptr))
        return finish(PHYSICALITY_DESCRIPTOR_INVALID);
    if (maximum_bytes < sizeof(physicality_descriptor_materialization)) {
        if (diagnostics != nullptr) {
            diagnostics->refusal_kind = PHYSICALITY_MATERIALIZATION_REFUSAL_MEMORY_GRANT;
            diagnostics->maximum_bytes = maximum_bytes;
            diagnostics->requested_bytes = sizeof(physicality_descriptor_materialization);
        }
        return finish(PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    }
    hash128_t current_floor;
    if (codepoint_table_copy_receipt(&current_floor) != 0)
        return finish(PHYSICALITY_DESCRIPTOR_MISSING_FLOOR);
    if (!hash128_equals(&current_floor, &vocabulary->floor_receipt)) return finish(PHYSICALITY_DESCRIPTOR_INVALID);
    if (!codepoint_table_id_index_ready()) return finish(PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    try {
        auto result = std::make_unique<physicality_descriptor_materialization>(maximum_bytes);
        const auto status = laplace_phys_desc_materialize_impl(*result, captured_source, *vocabulary,
            current_content_stages, current_stage_count, admitted_content_stages, admitted_stage_count,
            explicitly_missing_ids, missing_count,
            observation_sources, observation_source_count, observed_at_unix_us, cancellation, diagnostics);
        if (diagnostics != nullptr) {
            diagnostics->maximum_bytes = result->memory.used() + result->memory.remaining();
            diagnostics->retained_bytes = result->memory.used();
            diagnostics->peak_bytes = result->memory.peak();
        }
        *out_materialization = result.release();
        return finish(status);
    } catch (const AllocationRefusal& refusal) {
        if (diagnostics != nullptr) {
            diagnostics->refusal_kind = refusal.kind;
            diagnostics->maximum_bytes = refusal.limit;
            diagnostics->retained_bytes = refusal.retained;
            diagnostics->peak_bytes = refusal.peak;
            diagnostics->requested_bytes = refusal.requested;
        }
        return finish(PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    } catch (const Status& status) { return finish(status.value); }
      catch (const std::bad_alloc&) { return finish(PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED); }
}

extern "C" void physicality_descriptor_materialization_free(physicality_descriptor_materialization_t* value) {
    delete value;
}

extern "C" const hash128_t* physicality_descriptor_materialization_pending(
    const physicality_descriptor_materialization_t* value, size_t* count) {
    if (count != nullptr) *count = value == nullptr ? 0u : value->pending.size();
    return value == nullptr ? nullptr : value->pending.data();
}

extern "C" const physicality_descriptor_admitted_form_t* physicality_descriptor_materialization_forms(
    const physicality_descriptor_materialization_t* value, size_t* count) {
    if (count != nullptr) *count = value == nullptr ? 0u : value->forms.size();
    return value == nullptr ? nullptr : value->forms.data();
}

extern "C" const physicality_descriptor_form_observation_t* physicality_descriptor_materialization_observations(
    const physicality_descriptor_materialization_t* value, size_t* count) {
    if (count == nullptr) return nullptr;
    *count = value == nullptr ? 0u : value->observations.size();
    return value == nullptr || value->observations.empty() ? nullptr : value->observations.data();
}

extern "C" const hash128_t* physicality_descriptor_materialization_missing(
    const physicality_descriptor_materialization_t* value, size_t* count) {
    if (count != nullptr) *count = value == nullptr ? 0u : value->missing.size();
    return value == nullptr ? nullptr : value->missing.data();
}

extern "C" intent_stage_t* physicality_descriptor_materialization_take_stage(
    physicality_descriptor_materialization_t* value) {
    if (value == nullptr) return nullptr;
    intent_stage_t* stage = value->stage;
    value->stage = nullptr;
    value->memory.release(value->stage_bytes);
    value->stage_bytes = 0;
    return stage;
}

extern "C" size_t physicality_descriptor_materialization_bytes(
    const physicality_descriptor_materialization_t* value) {
    return value == nullptr ? 0u : sizeof(*value) + value->memory.used();
}

extern "C" size_t physicality_descriptor_materialization_peak_bytes(
    const physicality_descriptor_materialization_t* value) {
    return value == nullptr ? 0u : sizeof(*value) + value->memory.peak();
}
