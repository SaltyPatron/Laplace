#include "laplace/core/ordered_composition.h"

#include <limits.h>
#include <math.h>
#include <stdlib.h>

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

static int validate_requests(
    const laplace_ordered_composition_request_t* requests,
    size_t request_count,
    size_t* out_max_components) {
    size_t max_components = 0;
    for (size_t i = 0; i < request_count; ++i) {
        const laplace_ordered_composition_request_t* r = &requests[i];
        if (!r->components || r->component_count == 0
            || r->component_count > UINT32_MAX
            || r->component_count > INT32_MAX) return -1;
        for (size_t j = 0; j < r->component_count; ++j) {
            const laplace_ordered_component_t* child = &r->components[j];
            if ((child->tier == 0) != (child->has_atom != 0)) return -1;
            for (size_t k = 0; k < 4; ++k)
                if (!isfinite(child->coord[k])) return -1;
        }
        if (r->component_count > 1) {
            uint8_t max_tier = 0;
            for (size_t j = 0; j < r->component_count; ++j) {
                if (r->components[j].tier > max_tier) max_tier = r->components[j].tier;
            }
            if (max_tier == UINT8_MAX) return -2;
        }
        if (r->component_count > max_components) max_components = r->component_count;
    }
    *out_max_components = max_components;
    return 0;
}

typedef struct {
    hash128_t* ids;
    double* coords;
    size_t capacity;
} compose_scratch_t;

/* Identity deliberately excludes tier.  Each slot names the lowest-floor
 * representative of a root within this batch, so staging order cannot turn a
 * higher representation into the entity's retained floor or physicality. */
static int minimum_floor_representatives(
    const laplace_ordered_composition_result_t* results,
    size_t request_count,
    size_t** out_representatives) {
    if (request_count > (SIZE_MAX - 1) / 2
        || request_count > SIZE_MAX / sizeof(size_t)) return -3;
    size_t cap = 1;
    while (cap < request_count * 2) {
        if (cap > SIZE_MAX / 2) return -3;
        cap *= 2;
    }
    size_t* representatives = (size_t*)malloc(request_count * sizeof(*representatives));
    size_t* slots = (size_t*)malloc(cap * sizeof(*slots));
    if (!representatives || !slots) {
        free(representatives); free(slots);
        return -3;
    }
    for (size_t i = 0; i < cap; ++i) slots[i] = SIZE_MAX;
    for (size_t i = 0; i < request_count; ++i) {
        size_t slot = (size_t)(results[i].id.lo ^ results[i].id.hi) & (cap - 1);
        while (slots[slot] != SIZE_MAX
               && !hash128_equals(&results[slots[slot]].id, &results[i].id))
            slot = (slot + 1) & (cap - 1);
        if (slots[slot] == SIZE_MAX) {
            slots[slot] = i;
        } else if (results[i].tier < results[slots[slot]].tier) {
            slots[slot] = i;
        }
    }
    for (size_t i = 0; i < request_count; ++i) {
        size_t slot = (size_t)(results[i].id.lo ^ results[i].id.hi) & (cap - 1);
        while (!hash128_equals(&results[slots[slot]].id, &results[i].id))
            slot = (slot + 1) & (cap - 1);
        representatives[i] = slots[slot];
    }
    free(slots);
    *out_representatives = representatives;
    return 0;
}

static int compose_batch_validated(
    const laplace_ordered_composition_request_t* requests,
    size_t                                       request_count,
    laplace_ordered_composition_result_t*        out_results,
    compose_scratch_t*                           scratch) {
    for (size_t i = 0; i < request_count; ++i) {
        const laplace_ordered_composition_request_t* r = &requests[i];
        const size_t n = r->component_count;
        laplace_ordered_composition_result_t* result = &out_results[i];

        if (n == 1) {
            result->id = r->components[0].id;
            result->tier = r->components[0].tier;
            for (size_t k = 0; k < 4; ++k)
                result->coord[k] = r->components[0].coord[k];
            hilbert4d_encode(result->coord, &result->hilbert);
            continue;
        }

        uint8_t max_tier = 0;
        for (size_t j = 0; j < n; ++j) {
            const laplace_ordered_component_t* child = &r->components[j];
            scratch->ids[j] = child->id;
            for (size_t k = 0; k < 4; ++k)
                scratch->coords[j * 4 + k] = child->coord[k];
            if (child->tier > max_tier) max_tier = child->tier;
        }

        const uint8_t parent_tier = (uint8_t)(max_tier + 1);
        hash_composer_compose_node(
            parent_tier, scratch->ids, scratch->coords, n,
            &result->id, result->coord, &result->hilbert);
        result->tier = parent_tier;
    }
    return 0;
}

int laplace_ordered_composition_compose_batch(
    const laplace_ordered_composition_request_t* requests,
    size_t                                       request_count,
    laplace_ordered_composition_result_t*        out_results) {
    if (!requests || !out_results) return -1;
    if (request_count == 0) return 0;

    size_t max_components = 0;
    int rc = validate_requests(requests, request_count, &max_components);
    if (rc != 0 || max_components > SIZE_MAX / sizeof(hash128_t)
        || max_components > SIZE_MAX / (4 * sizeof(double))) return rc ? rc : -3;

    compose_scratch_t scratch = {
        .ids = (hash128_t*)malloc(max_components * sizeof(*scratch.ids)),
        .coords = (double*)malloc(max_components * 4 * sizeof(*scratch.coords)),
        .capacity = max_components,
    };
    if (!scratch.ids || !scratch.coords) {
        free(scratch.ids); free(scratch.coords);
        return -3;
    }
    rc = compose_batch_validated(requests, request_count, out_results, &scratch);
    free(scratch.ids); free(scratch.coords);
    return rc;
}

int laplace_ordered_composition_stage_batch(
    intent_stage_t*                              stage,
    const laplace_ordered_composition_request_t* requests,
    size_t                                       request_count,
    laplace_ordered_composition_result_t*        out_results) {
    if (!stage || !requests || !out_results) return -1;
    if (request_count == 0) return 0;

    size_t max_components = 0;
    int rc = validate_requests(requests, request_count, &max_components);
    if (rc != 0 || max_components > SIZE_MAX / sizeof(hash128_t)
        || max_components > SIZE_MAX / sizeof(uint64_t)
        || max_components > SIZE_MAX / (4 * sizeof(double))) return rc ? rc : -3;

    compose_scratch_t scratch = {
        .ids = (hash128_t*)malloc(max_components * sizeof(*scratch.ids)),
        .coords = (double*)malloc(max_components * 4 * sizeof(*scratch.coords)),
        .capacity = max_components,
    };
    uint64_t* child_flags = (uint64_t*)malloc(max_components * sizeof(*child_flags));
    double* trajectory = (double*)malloc(max_components * 4 * sizeof(*trajectory));
    size_t* representatives = NULL;
    if (!scratch.ids || !scratch.coords || !child_flags || !trajectory) {
        free(scratch.ids); free(scratch.coords); free(child_flags); free(trajectory);
        return -3;
    }
    rc = compose_batch_validated(requests, request_count, out_results, &scratch);
    if (rc != 0) goto done;

    rc = minimum_floor_representatives(out_results, request_count, &representatives);
    if (rc != 0) goto done;

    for (size_t i = 0; i < request_count; ++i) {
        const laplace_ordered_composition_request_t* r = &requests[i];
        const size_t n = r->component_count;
        const laplace_ordered_composition_result_t* result = &out_results[i];
        if (n == 1 || representatives[i] != i) continue;

        if (intent_stage_witness_seen(stage, &result->id)) {
            if (intent_stage_lower_entity_tier(stage, &result->id, (int16_t)result->tier) < 0) {
                rc = -3;
                break;
            }
            continue;
        }
        for (size_t j = 0; j < n; ++j) {
            const laplace_ordered_component_t* child = &r->components[j];
            scratch.ids[j] = child->id;
            child_flags[j] = laplace_vertex_flags(
                child->tier, child->has_atom != 0, child->atom);
        }
        size_t trajectory_vertices = 0;
        if (trajectory_build_flagged_rle(
                scratch.ids, child_flags, n, trajectory, &trajectory_vertices) != 0
            || trajectory_vertices > UINT32_MAX) {
            rc = -3;
            break;
        }

        if (intent_stage_add_entity(
                stage, &result->id, (int16_t)result->tier,
                &r->type_id, &r->source_id) != 0) {
            rc = -3;
            break;
        }
        hash128_t physicality_id;
        laplace_physicality_id_compute(result->id, 1, &physicality_id);
        if (intent_stage_add_physicality(
                stage, &physicality_id, &result->id, 1,
                result->coord, &result->hilbert,
                trajectory, (uint32_t)trajectory_vertices, (int32_t)n,
                1, 0.0, 1, 0, r->observed_at_unix_us) != 0) {
            rc = -3;
            break;
        }
        if (intent_stage_witness_record(stage, &result->id) != 0) {
            rc = -3;
            break;
        }
    }

done:
    free(representatives);
    free(scratch.ids); free(scratch.coords); free(child_flags); free(trajectory);
    return rc;
}
