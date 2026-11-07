#include "physicality_descriptor_internal.h"

#include <stdlib.h>
#include <string.h>

static physicality_descriptor_status_t same_body(
    const physicality_descriptor_input_t* a, const physicality_descriptor_input_t* b,
    const physicality_descriptor_cancel_t* cancellation, int* equal) {
    *equal = 0;
    if (!hash128_equals(&a->entity_id, &b->entity_id) || a->type != b->type ||
        memcmp(a->coord, b->coord, sizeof(a->coord)) != 0 ||
        memcmp(&a->hilbert_index, &b->hilbert_index, sizeof(a->hilbert_index)) != 0 ||
        a->trajectory_vertices != b->trajectory_vertices ||
        a->n_constituents != b->n_constituents ||
        a->alignment_residual_is_null != b->alignment_residual_is_null ||
        a->source_dim_is_null != b->source_dim_is_null ||
        (!a->alignment_residual_is_null &&
            memcmp(&a->alignment_residual, &b->alignment_residual, sizeof(a->alignment_residual)) != 0) ||
        (!a->source_dim_is_null && a->source_dim != b->source_dim))
        return PHYSICALITY_DESCRIPTOR_OK;
    if (a->trajectory_vertices != 0u &&
        (a->trajectory_xyzm == NULL || b->trajectory_xyzm == NULL))
        return PHYSICALITY_DESCRIPTOR_OK;
    for (size_t vertex = 0u; vertex < a->trajectory_vertices; ++vertex) {
        if (physicality_descriptor_cancel_requested(cancellation))
            return PHYSICALITY_DESCRIPTOR_CANCELLED;
        if (memcmp(a->trajectory_xyzm + vertex * 4u,
                b->trajectory_xyzm + vertex * 4u, 4u * sizeof(double)) != 0)
            return PHYSICALITY_DESCRIPTOR_OK;
    }
    *equal = 1;
    return PHYSICALITY_DESCRIPTOR_OK;
}

static physicality_descriptor_status_t repeat_body_references(
    physicality_descriptor_plan_t* plan, size_t first, size_t count, size_t input_index) {
    if (first > plan->reference_count || count > plan->reference_count - first ||
        count > plan->reference_capacity - plan->reference_count)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    for (size_t offset = 0u; offset < count; ++offset) {
        if (physicality_descriptor_cancel_requested(plan->cancellation))
            return PHYSICALITY_DESCRIPTOR_CANCELLED;
        physicality_descriptor_reference_t* value = &plan->references[plan->reference_count++];
        *value = plan->references[first + offset];
        value->input_index = input_index;
    }
    return PHYSICALITY_DESCRIPTOR_OK;
}

typedef struct {
    size_t input_plus_one, reference_first, reference_count;
} body_cache_entry_t;

typedef struct {
    body_cache_entry_t* entries;
    size_t buckets, bytes, maximum_bytes;
} body_cache_t;

enum { BODY_CACHE_WAYS = 4, BODY_CACHE_MAX_BUCKETS = 256 };

static void body_cache_release(physicality_descriptor_plan_t* plan, body_cache_t* cache) {
    if (cache->entries == NULL) return;
    free(cache->entries);
    plan->bytes -= cache->bytes;
    cache->entries = NULL;
    cache->bytes = 0u;
}

static int body_cache_growth_fits(
    const physicality_descriptor_plan_t* plan, const body_cache_t* cache, size_t vertices) {
    size_t nodes = plan->node_count, children = plan->child_count;
    size_t nc = plan->node_capacity == 0u ? 1u : plan->node_capacity;
    size_t cc = plan->child_capacity == 0u ? 1u : plan->child_capacity;
    size_t sc = plan->slot_count;
    size_t retained = plan->bytes, nb = 0u, cb = 0u, sb = 0u, replacement = 0u;
    if (!checked_add(&nodes, 14u) || !checked_array(&nodes, vertices, 5u) ||
        !checked_add(&children, 95u) || !checked_array(&children, vertices, 42u) ||
        nodes > SIZE_MAX / 2u)
        return 0;
    while (nc < nodes) { if (nc > SIZE_MAX / 2u) return 0; nc *= 2u; }
    while (cc < children) { if (cc > SIZE_MAX / 2u) return 0; cc *= 2u; }
    while (sc < nodes * 2u) { if (sc > SIZE_MAX / 2u) return 0; sc *= 2u; }
    if (!checked_array(&nb, nc, sizeof(*plan->nodes)) ||
        !checked_array(&cb, cc, sizeof(*plan->children)) ||
        !checked_array(&sb, sc, sizeof(*plan->slots)))
        return 0;
    retained -= plan->node_capacity * sizeof(*plan->nodes);
    retained -= plan->child_capacity * sizeof(*plan->children);
    retained -= plan->slot_count * sizeof(*plan->slots);
    if (!checked_add(&retained, nb) || !checked_add(&retained, cb) ||
        !checked_add(&retained, sb))
        return 0;
    if (nc > plan->node_capacity) replacement = nb;
    if (cc > plan->child_capacity && cb > replacement) replacement = cb;
    if (sc > plan->slot_count && sb > replacement) replacement = sb;
    return checked_add(&retained, replacement) && retained <= cache->maximum_bytes;
}

static void body_cache_initialize(physicality_descriptor_plan_t* plan,
    size_t input_count, size_t vertices, size_t maximum_vertices, body_cache_t* cache) {
    size_t bound;
    memset(cache, 0, sizeof(*cache));
    if (input_count < 3u) return;
    cache->buckets = 1u;
    while (cache->buckets < BODY_CACHE_MAX_BUCKETS &&
        cache->buckets < input_count / BODY_CACHE_WAYS)
        cache->buckets *= 2u;
    cache->bytes = cache->buckets * BODY_CACHE_WAYS * sizeof(*cache->entries);
    cache->maximum_bytes = plan->maximum_bytes;
    if (physicality_descriptor_plan_payload_bound(
            input_count, vertices, maximum_vertices, &bound) == PHYSICALITY_DESCRIPTOR_OK &&
        bound < cache->maximum_bytes)
        cache->maximum_bytes = bound;
    if (plan->bytes > cache->maximum_bytes ||
        cache->bytes > cache->maximum_bytes - plan->bytes)
        return;
    cache->entries = calloc(cache->buckets * BODY_CACHE_WAYS, sizeof(*cache->entries));
    if (cache->entries == NULL) return;
    plan->bytes += cache->bytes;
    if (plan->bytes > plan->peak_bytes) plan->peak_bytes = plan->bytes;
}

static size_t body_cache_bucket(
    const physicality_descriptor_input_t* input, const body_cache_t* cache) {
    hash128_t route = input->entity_id;
    route.lo ^= (uint64_t)(uint16_t)input->type << 32u;
    route.hi ^= (uint64_t)input->trajectory_vertices;
    route.hi ^= (uint64_t)(uint32_t)input->n_constituents << 32u;
    return identity_slot(&route, cache->buckets - 1u) * BODY_CACHE_WAYS;
}

void physicality_descriptor_plan_free(physicality_descriptor_plan_t* plan) {
    if (plan == NULL) return;
    free(plan->nodes);
    free(plan->children);
    free(plan->roots);
    free(plan->references);
    free(plan->slots);
    free(plan->trajectory_children);
    free(plan);
}

physicality_descriptor_status_t physicality_descriptor_plan_build(
    const physicality_descriptor_input_t* inputs, size_t input_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* limits,
    physicality_descriptor_plan_t** out_plan) {
    return physicality_descriptor_plan_build_cancelable(
        inputs, input_count, basis, limits, NULL, out_plan);
}

physicality_descriptor_status_t physicality_descriptor_plan_build_cancelable(
    const physicality_descriptor_input_t* inputs, size_t input_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* limits,
    const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_plan_t** out_plan) {
    return physicality_descriptor_plan_build_diagnosed_cancelable(
        inputs, input_count, basis, limits, cancellation, NULL, out_plan);
}

physicality_descriptor_status_t physicality_descriptor_plan_build_diagnosed_cancelable(
    const physicality_descriptor_input_t* inputs, size_t input_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* limits,
    const physicality_descriptor_cancel_t* cancellation,
    physicality_descriptor_plan_diagnostics_t* diagnostics,
    physicality_descriptor_plan_t** out_plan) {
    size_t vertices = 0u, widest = 17u, maximum_vertices = 0u;
    size_t references = input_count, bytes = sizeof(physicality_descriptor_plan_t);
    physicality_descriptor_plan_t* plan;
    if (diagnostics != NULL) {
        memset(diagnostics, 0, sizeof(*diagnostics));
        diagnostics->input_count = input_count;
        diagnostics->maximum_bytes = limits == NULL ? 0u : limits->maximum_plan_bytes;
    }
    if (out_plan == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_plan = NULL;
    if (physicality_descriptor_cancel_requested(cancellation)) return PHYSICALITY_DESCRIPTOR_CANCELLED;
    if ((input_count != 0u && inputs == NULL) ||
        !physicality_descriptor_basis_is_valid(basis) || limits == NULL)
        return PHYSICALITY_DESCRIPTOR_INVALID;
    if (input_count > SIZE_MAX / sizeof(*inputs)) {
        plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL,
            PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    for (size_t input = 0; input < input_count; ++input) {
        if (physicality_descriptor_cancel_requested(cancellation)) return PHYSICALITY_DESCRIPTOR_CANCELLED;
        size_t width = inputs[input].trajectory_vertices;
        if (!checked_add(&vertices, width) || width > SIZE_MAX / (4u * sizeof(double)) ||
            width == SIZE_MAX) {
            plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL,
                PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
            return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
        }
        if (width + 1u > widest) widest = width + 1u;
        if (width > maximum_vertices) maximum_vertices = width;
    }
    if (!checked_add(&references, vertices) ||
        !checked_array(&bytes, input_count, sizeof(hash128_t)) ||
        !checked_array(&bytes, references, sizeof(physicality_descriptor_reference_t)) ||
        !checked_array(&bytes, 1u, sizeof(size_t)) ||
        !checked_array(&bytes, widest, sizeof(hash128_t))) {
        plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL,
            PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    if (bytes > limits->maximum_plan_bytes) {
        plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL,
            PHYSICALITY_DESCRIPTOR_PLAN_GRANT_REFUSED, bytes);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    plan = calloc(1u, sizeof(*plan));
    if (plan == NULL) {
        plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL,
            PHYSICALITY_DESCRIPTOR_PLAN_ALLOCATOR_REFUSED, bytes);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    if (input_count != 0u) plan->roots = calloc(input_count, sizeof(*plan->roots));
    if (references != 0u) plan->references = calloc(references, sizeof(*plan->references));
    plan->slots = calloc(1u, sizeof(*plan->slots));
    plan->trajectory_children = calloc(widest, sizeof(hash128_t));
    if ((input_count != 0u && !plan->roots) || (references != 0u && !plan->references) ||
        !plan->slots || !plan->trajectory_children) {
        plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL,
            PHYSICALITY_DESCRIPTOR_PLAN_ALLOCATOR_REFUSED, bytes);
        physicality_descriptor_plan_free(plan);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    plan->cancellation = cancellation;
    plan->reference_capacity = references;
    plan->root_count = input_count;
    plan->slot_count = 1u;
    plan->scratch_capacity = widest;
    plan->bytes = plan->peak_bytes = bytes;
    plan->maximum_bytes = limits->maximum_plan_bytes;
    body_cache_t cache;
    body_cache_initialize(plan, input_count, vertices, maximum_vertices, &cache);
    size_t previous_reference_first = 0u, previous_reference_count = 0u;
    for (size_t input = 0; input < input_count; ++input) {
        const size_t reference_first = plan->reference_count;
        size_t template_input = input == 0u ? 0u : input - 1u;
        size_t template_first = previous_reference_first;
        size_t template_count = previous_reference_count;
        size_t cache_slot = SIZE_MAX;
        int equal = 0;
        physicality_descriptor_status_t status = physicality_descriptor_cancel_requested(cancellation)
            ? PHYSICALITY_DESCRIPTOR_CANCELLED : PHYSICALITY_DESCRIPTOR_OK;
        if (status == PHYSICALITY_DESCRIPTOR_OK && input != 0u)
            status = same_body(&inputs[input], &inputs[input - 1u], cancellation, &equal);
        if (status == PHYSICALITY_DESCRIPTOR_OK && !equal && cache.entries != NULL) {
            const size_t bucket = body_cache_bucket(&inputs[input], &cache);
            size_t empty = SIZE_MAX;
            for (size_t way = 0u; way < BODY_CACHE_WAYS; ++way) {
                const size_t slot = bucket + way;
                const body_cache_entry_t* entry = &cache.entries[slot];
                if (physicality_descriptor_cancel_requested(cancellation)) {
                    status = PHYSICALITY_DESCRIPTOR_CANCELLED;
                    break;
                }
                if (entry->input_plus_one == 0u) {
                    if (empty == SIZE_MAX) empty = slot;
                    continue;
                }
                status = same_body(&inputs[input], &inputs[entry->input_plus_one - 1u],
                    cancellation, &equal);
                if (status != PHYSICALITY_DESCRIPTOR_OK || equal) {
                    if (equal) {
                        template_input = entry->input_plus_one - 1u;
                        template_first = entry->reference_first;
                        template_count = entry->reference_count;
                    }
                    break;
                }
            }
            if (!equal)
                cache_slot = empty != SIZE_MAX ? empty :
                    bucket + (input / cache.buckets) % BODY_CACHE_WAYS;
        }
        if (status == PHYSICALITY_DESCRIPTOR_OK) {
            if (equal) {
                status = repeat_body_references(plan, template_first, template_count, input);
                if (status == PHYSICALITY_DESCRIPTOR_OK)
                    plan->roots[input] = plan->roots[template_input];
            } else {
                if (cache.entries != NULL &&
                    !body_cache_growth_fits(plan, &cache, inputs[input].trajectory_vertices))
                    body_cache_release(plan, &cache);
                status = laplace_phys_desc_describe_one(plan, diagnostics, basis, &inputs[input], input);
                if (status == PHYSICALITY_DESCRIPTOR_OK && cache.entries != NULL &&
                    cache_slot != SIZE_MAX) {
                    body_cache_entry_t* entry = &cache.entries[cache_slot];
                    entry->input_plus_one = input + 1u;
                    entry->reference_first = reference_first;
                    entry->reference_count = plan->reference_count - reference_first;
                }
            }
        }
        if (status != PHYSICALITY_DESCRIPTOR_OK) {
            body_cache_release(plan, &cache);
            plan_snapshot(plan, input, diagnostics);
            physicality_descriptor_plan_free(plan);
            return status;
        }
        previous_reference_first = reference_first;
        previous_reference_count = plan->reference_count - reference_first;
    }
    body_cache_release(plan, &cache);
    plan_snapshot(plan, input_count, diagnostics);
    plan->cancellation = NULL;
    *out_plan = plan;
    return PHYSICALITY_DESCRIPTOR_OK;
}

size_t physicality_descriptor_plan_bytes(const physicality_descriptor_plan_t* plan) {
    return plan == NULL ? 0u : plan->bytes;
}

size_t physicality_descriptor_plan_peak_bytes(const physicality_descriptor_plan_t* plan) {
    return plan == NULL ? 0u : plan->peak_bytes;
}

#define VIEW(name, member, count_member, type) \
const type* name(const physicality_descriptor_plan_t* plan, size_t* count) { \
    if (count != NULL) *count = plan == NULL ? 0u : plan->count_member; \
    return plan == NULL ? NULL : plan->member; \
}
VIEW(physicality_descriptor_plan_nodes, nodes, node_count, physicality_descriptor_node_t)
VIEW(physicality_descriptor_plan_children, children, child_count, hash128_t)
VIEW(physicality_descriptor_plan_roots, roots, root_count, hash128_t)
VIEW(physicality_descriptor_plan_references, references, reference_count, physicality_descriptor_reference_t)
