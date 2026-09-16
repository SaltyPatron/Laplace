#include "laplace/core/physicality_descriptor.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/trajectory.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/content_witness_batch.h"

struct physicality_descriptor_capture {
    physicality_descriptor_input_t* inputs;
    physicality_descriptor_observation_t* observations;
    double* trajectories;
    physicality_descriptor_plan_t* plan;
    size_t count;
    size_t bytes;
    size_t peak_bytes;
};

typedef struct {
    const uint8_t* bytes[10];
    int32_t lengths[10];
} fields_t;

static uint64_t read_word(const uint8_t* bytes, size_t width, int little_endian) {
    uint64_t result = 0;
    for (size_t i = 0; i < width; ++i) {
        const size_t index = little_endian ? width - i - 1u : i;
        result = (result << 8u) | bytes[index];
    }
    return result;
}

static double read_double(const uint8_t* bytes, int little_endian) {
    const uint64_t word = read_word(bytes, 8u, little_endian);
    double value;
    memcpy(&value, &word, sizeof(value));
    return value;
}

static int read_fields(const uint8_t* data, size_t bytes, size_t* offset, fields_t* fields) {
    size_t cursor = *offset;
    if (cursor > bytes || bytes - cursor < 2u || read_word(data + cursor, 2u, 0) != 10u)
        return 0;
    cursor += 2u;
    for (size_t i = 0; i < 10u; ++i) {
        uint32_t size;
        if (bytes - cursor < 4u) return 0;
        size = (uint32_t)read_word(data + cursor, 4u, 0);
        cursor += 4u;
        fields->lengths[i] = size == UINT32_MAX ? -1 : (int32_t)size;
        fields->bytes[i] = size == UINT32_MAX ? NULL : data + cursor;
        if (size != UINT32_MAX) {
            if (size > INT32_MAX || size > bytes - cursor) return 0;
            cursor += size;
        }
    }
    *offset = cursor;
    return 1;
}

static int geometry(const uint8_t* bytes, int32_t length, int allow_null,
    size_t* vertices, const uint8_t** coordinates) {
    uint32_t type;
    size_t header;
    if (length == -1 && allow_null) {
        *vertices = 0u;
        *coordinates = NULL;
        return 1;
    }
    if (length < 5 || bytes[0] != 1u) return 0;
    type = (uint32_t)read_word(bytes + 1u, 4u, 1);
    if (type == UINT32_C(0xc0000001)) {
        *vertices = 1u;
        header = 5u;
    } else if (type == UINT32_C(0xc0000002) && length >= 9 && allow_null) {
        *vertices = (size_t)read_word(bytes + 5u, 4u, 1);
        header = 9u;
        if (*vertices < 2u) return 0;
    } else return 0;
    if (*vertices > (SIZE_MAX - header) / 32u || header + *vertices * 32u != (size_t)length)
        return 0;
    *coordinates = bytes + header;
    return 1;
}

static int array_bytes(size_t* value, size_t count, size_t width) {
    if (count > (SIZE_MAX - *value) / width) return 0;
    *value += count * width;
    return 1;
}

static int validate_fields(const fields_t* fields, size_t* trajectory_vertices) {
    const int32_t* n = fields->lengths;
    size_t coordinate_vertices;
    const uint8_t* coordinates;
    return n[0] == 16 && n[1] == 16 && n[2] == 2 && n[4] == 16 && n[6] == 4 &&
        (n[7] == -1 || n[7] == 8) && (n[8] == -1 || n[8] == 4) && n[9] == 8 &&
        geometry(fields->bytes[3], n[3], 0, &coordinate_vertices, &coordinates) &&
        coordinate_vertices == 1u &&
        geometry(fields->bytes[5], n[5], 1, trajectory_vertices, &coordinates);
}

physicality_descriptor_status_t physicality_descriptor_stages_preflight(
    const intent_stage_t* const* stages, size_t stage_count,
    size_t maximum_logical_occurrences, size_t* out_body_count,
    size_t* out_stored_vertices, size_t* out_logical_occurrences) {
    return physicality_descriptor_stages_preflight_cancelable(stages, stage_count,
        maximum_logical_occurrences, NULL, out_body_count, out_stored_vertices, out_logical_occurrences);
}

physicality_descriptor_status_t physicality_descriptor_stages_preflight_cancelable(
    const intent_stage_t* const* stages, size_t stage_count,
    size_t maximum_logical_occurrences,
    const physicality_descriptor_cancel_t* cancellation, size_t* out_body_count,
    size_t* out_stored_vertices, size_t* out_logical_occurrences) {
    size_t bodies = 0u, stored = 0u, logical = 0u;
    if (physicality_descriptor_cancel_requested(cancellation)) return PHYSICALITY_DESCRIPTOR_CANCELLED;
    if (stage_count != 0u && stages == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    for (size_t stage = 0u; stage < stage_count; ++stage) {
        size_t length, offset = 0u, rows = 0u;
        if (stages[stage] == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
        const uint8_t* data = intent_stage_tuple_ptr(stages[stage], INTENT_STAGE_TABLE_PHYSICALITIES, &length);
        while (offset < length) {
            if (physicality_descriptor_cancel_requested(cancellation)) return PHYSICALITY_DESCRIPTOR_CANCELLED;
            fields_t fields;
            size_t vertices, row_logical = 0u;
            int typed_payload = 0;
            const uint8_t* coordinates;
            if (!read_fields(data, length, &offset, &fields) || !validate_fields(&fields, &vertices) ||
                !geometry(fields.bytes[5], fields.lengths[5], 1, &vertices, &coordinates))
                return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
            const int16_t type = (int16_t)read_word(fields.bytes[2], 2u, 0);
            const int canonical_manifest = type == 1 || type == PHYSICALITY_DESCRIPTOR_RETENTION_TYPE;
            if (type <= 0) return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
            for (size_t vertex = 0u; vertex < vertices; ++vertex) {
                if (physicality_descriptor_cancel_requested(cancellation)) return PHYSICALITY_DESCRIPTOR_CANCELLED;
                double packed[4];
                size_t run;
                int typed_vertex;
                for (size_t axis = 0u; axis < 4u; ++axis)
                    packed[axis] = read_double(coordinates + vertex * 32u + axis * 8u, 1);
                if (trajectory_manifest_scan(packed, 1u, &run, &typed_vertex) != 0 ||
                    !array_bytes(&row_logical, run, 1u)) return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
                typed_payload |= typed_vertex;
                if (canonical_manifest && typed_payload) return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
                if (canonical_manifest && row_logical > maximum_logical_occurrences - logical)
                    return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
            }
            const int32_t declared = (int32_t)read_word(fields.bytes[6], 4u, 0);
            if (declared < 0 || (type == PHYSICALITY_DESCRIPTOR_RETENTION_TYPE && declared < 2) ||
                (!typed_payload && row_logical != (size_t)declared))
                return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
            if (bodies == SIZE_MAX || !array_bytes(&stored, vertices, 1u))
                return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
            if (canonical_manifest) logical += row_logical;
            ++bodies; ++rows;
        }
        if (rows != intent_stage_physicality_count(stages[stage])) return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    }
    if (out_body_count != NULL) *out_body_count = bodies;
    if (out_stored_vertices != NULL) *out_stored_vertices = stored;
    if (out_logical_occurrences != NULL) *out_logical_occurrences = logical;
    return PHYSICALITY_DESCRIPTOR_OK;
}

static int decode_fields(const fields_t* fields, physicality_descriptor_input_t* input,
    physicality_descriptor_observation_t* observation, double* trajectory,
    const physicality_descriptor_cancel_t* cancellation) {
    size_t vertices;
    const uint8_t* coordinates;
    hash128_t expected;
    uint64_t timestamp_word;
    int64_t timestamp;
    memcpy(&observation->placement_id, fields->bytes[0], 16u);
    memcpy(&input->entity_id, fields->bytes[1], 16u);
    input->type = (int16_t)read_word(fields->bytes[2], 2u, 0);
    if (!geometry(fields->bytes[3], fields->lengths[3], 0, &vertices, &coordinates)) return 0;
    for (size_t axis = 0; axis < 4u; ++axis)
        input->coord[axis] = read_double(coordinates + axis * 8u, 1);
    memcpy(&input->hilbert_index, fields->bytes[4], 16u);
    if (!geometry(fields->bytes[5], fields->lengths[5], 1, &vertices, &coordinates)) return 0;
    for (size_t component = 0; component < vertices * 4u; ++component) {
        if (physicality_descriptor_cancel_requested(cancellation)) return -1;
        trajectory[component] = read_double(coordinates + component * 8u, 1);
    }
    input->trajectory_xyzm = vertices == 0u ? NULL : trajectory;
    input->trajectory_vertices = vertices;
    input->n_constituents = (int32_t)read_word(fields->bytes[6], 4u, 0);
    input->alignment_residual_is_null = fields->lengths[7] == -1;
    if (!input->alignment_residual_is_null)
        input->alignment_residual = read_double(fields->bytes[7], 0);
    input->source_dim_is_null = fields->lengths[8] == -1;
    if (!input->source_dim_is_null)
        input->source_dim = (int32_t)read_word(fields->bytes[8], 4u, 0);
    timestamp_word = read_word(fields->bytes[9], 8u, 0);
    memcpy(&timestamp, &timestamp_word, sizeof(timestamp));
    if (timestamp > INT64_MAX - INTENT_STAGE_PG_EPOCH_UNIX_US) return 0;
    observation->observed_at_unix_us = timestamp + INTENT_STAGE_PG_EPOCH_UNIX_US;
    laplace_physicality_id_compute(input->entity_id, input->type, &expected);
    return hash128_equals(&expected, &observation->placement_id);
}

size_t physicality_descriptor_capture_release_plan(physicality_descriptor_capture_t* capture) {
    size_t released;
    if (capture == NULL || capture->plan == NULL) return 0u;
    released = physicality_descriptor_plan_bytes(capture->plan);
    physicality_descriptor_plan_free(capture->plan);
    capture->plan = NULL;
    capture->bytes -= released;
    return released;
}

void physicality_descriptor_capture_free(physicality_descriptor_capture_t* capture) {
    if (capture == NULL) return;
    physicality_descriptor_plan_free(capture->plan);
    free(capture->inputs);
    free(capture->observations);
    free(capture->trajectories);
    free(capture);
}

static physicality_descriptor_status_t capture_stage_rows_cancelable(
    const intent_stage_t* const* stages, size_t stage_count,
    size_t maximum_capture_bytes, const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_capture_t** out_capture) {
    size_t count = 0u, vertices = 0u, bytes = sizeof(physicality_descriptor_capture_t);
    physicality_descriptor_capture_t* capture;
    if (out_capture == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_capture = NULL;
    if (physicality_descriptor_cancel_requested(cancellation)) return PHYSICALITY_DESCRIPTOR_CANCELLED;
    if (stage_count != 0u && stages == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    for (size_t stage = 0; stage < stage_count; ++stage) {
        size_t length, offset = 0u, rows = 0u;
        const uint8_t* data;
        if (stages[stage] == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
        data = intent_stage_tuple_ptr(stages[stage], INTENT_STAGE_TABLE_PHYSICALITIES, &length);
        while (offset < length) {
            if (physicality_descriptor_cancel_requested(cancellation)) return PHYSICALITY_DESCRIPTOR_CANCELLED;
            fields_t fields;
            size_t width;
            if (!read_fields(data, length, &offset, &fields) || !validate_fields(&fields, &width))
                return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
            if (count == SIZE_MAX || !array_bytes(&vertices, width, 1u))
                return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
            ++count;
            ++rows;
        }
        if (rows != intent_stage_physicality_count(stages[stage]))
            return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    }
    if (!array_bytes(&bytes, count, sizeof(physicality_descriptor_input_t)) ||
        !array_bytes(&bytes, count, sizeof(physicality_descriptor_observation_t)) ||
        !array_bytes(&bytes, vertices, 4u * sizeof(double)) || bytes > maximum_capture_bytes)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    capture = calloc(1u, sizeof(*capture));
    if (capture == NULL) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    if (count != 0u) {
        capture->inputs = calloc(count, sizeof(*capture->inputs));
        capture->observations = calloc(count, sizeof(*capture->observations));
    }
    if (vertices != 0u) capture->trajectories = calloc(vertices, 4u * sizeof(double));
    if ((count != 0u && (capture->inputs == NULL || capture->observations == NULL)) ||
        (vertices != 0u && capture->trajectories == NULL)) {
        physicality_descriptor_capture_free(capture);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    capture->count = count;
    capture->bytes = bytes;
    capture->peak_bytes = bytes;
    count = 0u;
    vertices = 0u;
    for (size_t stage = 0; stage < stage_count; ++stage) {
        size_t length, offset = 0u, row = 0u;
        const uint8_t* data = intent_stage_tuple_ptr(
            stages[stage], INTENT_STAGE_TABLE_PHYSICALITIES, &length);
        while (offset < length) {
            if (physicality_descriptor_cancel_requested(cancellation)) {
                physicality_descriptor_capture_free(capture);
                return PHYSICALITY_DESCRIPTOR_CANCELLED;
            }
            fields_t fields;
            physicality_descriptor_input_t* input = &capture->inputs[count];
            physicality_descriptor_observation_t* observation = &capture->observations[count];
            observation->source_stage_index = stage;
            observation->source_row_index = row++;
            int decoded = read_fields(data, length, &offset, &fields) ?
                decode_fields(&fields, input, observation, capture->trajectories == NULL ? NULL :
                    capture->trajectories + vertices * 4u, cancellation) : 0;
            if (decoded <= 0) {
                physicality_descriptor_capture_free(capture);
                return decoded < 0 ? PHYSICALITY_DESCRIPTOR_CANCELLED : PHYSICALITY_DESCRIPTOR_INVALID_BODY;
            }
            vertices += input->trajectory_vertices;
            ++count;
        }
    }
    *out_capture = capture;
    return PHYSICALITY_DESCRIPTOR_OK;
}

physicality_descriptor_status_t physicality_descriptor_capture_stage_rows(
    const intent_stage_t* const* stages, size_t stage_count,
    size_t maximum_capture_bytes, physicality_descriptor_capture_t** out_capture) {
    return capture_stage_rows_cancelable(stages, stage_count, maximum_capture_bytes, NULL, out_capture);
}

physicality_descriptor_status_t physicality_descriptor_capture_stages(
    const intent_stage_t* const* stages, size_t stage_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* plan_limits,
    size_t maximum_capture_bytes, physicality_descriptor_capture_t** out_capture) {
    return physicality_descriptor_capture_stages_cancelable(stages, stage_count, basis,
        plan_limits, maximum_capture_bytes, NULL, out_capture);
}

physicality_descriptor_status_t physicality_descriptor_capture_stages_cancelable(
    const intent_stage_t* const* stages, size_t stage_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* plan_limits,
    size_t maximum_capture_bytes, const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_capture_t** out_capture) {
    physicality_descriptor_capture_t* capture = NULL;
    physicality_descriptor_status_t status;
    physicality_descriptor_limits_t remaining_limits;
    if (out_capture == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_capture = NULL;
    if (!physicality_descriptor_basis_is_valid(basis) || plan_limits == NULL)
        return PHYSICALITY_DESCRIPTOR_INVALID;
    status = capture_stage_rows_cancelable(
        stages, stage_count, maximum_capture_bytes, cancellation, &capture);
    if (status != PHYSICALITY_DESCRIPTOR_OK) return status;
    remaining_limits = *plan_limits;
    if (remaining_limits.maximum_plan_bytes > maximum_capture_bytes - capture->bytes)
        remaining_limits.maximum_plan_bytes = maximum_capture_bytes - capture->bytes;
    status = physicality_descriptor_plan_build_cancelable(capture->inputs, capture->count,
        basis, &remaining_limits, cancellation, &capture->plan);
    if (status != PHYSICALITY_DESCRIPTOR_OK) {
        physicality_descriptor_capture_free(capture);
        return status;
    }
    capture->peak_bytes = capture->bytes + physicality_descriptor_plan_peak_bytes(capture->plan);
    capture->bytes += physicality_descriptor_plan_bytes(capture->plan);
    *out_capture = capture;
    return PHYSICALITY_DESCRIPTOR_OK;
}

size_t physicality_descriptor_capture_bytes(const physicality_descriptor_capture_t* capture) {
    return capture == NULL ? 0u : capture->bytes;
}

size_t physicality_descriptor_capture_peak_bytes(const physicality_descriptor_capture_t* capture) {
    return capture == NULL ? 0u : capture->peak_bytes;
}

const physicality_descriptor_plan_t* physicality_descriptor_capture_plan(
    const physicality_descriptor_capture_t* capture) {
    return capture == NULL ? NULL : capture->plan;
}

const physicality_descriptor_input_t* physicality_descriptor_capture_inputs(
    const physicality_descriptor_capture_t* capture, size_t* count) {
    if (count != NULL) *count = capture == NULL ? 0u : capture->count;
    return capture == NULL ? NULL : capture->inputs;
}

const physicality_descriptor_observation_t* physicality_descriptor_capture_observations(
    const physicality_descriptor_capture_t* capture, size_t* count) {
    if (count != NULL) *count = capture == NULL ? 0u : capture->count;
    return capture == NULL ? NULL : capture->observations;
}

physicality_descriptor_status_t physicality_descriptor_stage_add_batch(
    intent_stage_t* stage, const physicality_descriptor_input_t* inputs,
    const hash128_t* declared_physicality_ids,
    const int64_t* observed_at_unix_us, size_t count) {
    if (stage == NULL || (count != 0u && (inputs == NULL || observed_at_unix_us == NULL)))
        return PHYSICALITY_DESCRIPTOR_INVALID;
    if (count > SIZE_MAX / sizeof(*inputs)) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    for (size_t i = 0; i < count; ++i) {
        const physicality_descriptor_input_t* input = &inputs[i];
        hash128_t placement;
        if (input->trajectory_vertices > (INT32_MAX - 9u) / 32u)
            return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
        if (declared_physicality_ids == NULL)
            laplace_physicality_id_compute(input->entity_id, input->type, &placement);
        else placement = declared_physicality_ids[i];
        if (intent_stage_add_physicality(stage, &placement, &input->entity_id, input->type,
            input->coord, &input->hilbert_index, input->trajectory_xyzm,
            (uint32_t)input->trajectory_vertices, input->n_constituents,
            input->alignment_residual_is_null, input->alignment_residual,
            input->source_dim_is_null, input->source_dim, observed_at_unix_us[i]) != 0)
            return intent_stage_allocation_failed(stage) ? PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED :
                PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    }
    return PHYSICALITY_DESCRIPTOR_OK;
}
