#ifndef LAPLACE_CORE_PHYSICALITY_DESCRIPTOR_INTERNAL_H
#define LAPLACE_CORE_PHYSICALITY_DESCRIPTOR_INTERNAL_H

#include "laplace/core/physicality_descriptor.h"

#include <stddef.h>
#include <stdint.h>

struct physicality_descriptor_plan {
    physicality_descriptor_node_t* nodes;
    hash128_t* children;
    hash128_t* roots;
    physicality_descriptor_reference_t* references;
    size_t* slots;
    hash128_t* trajectory_children;
    size_t node_count, node_capacity;
    size_t child_count, child_capacity;
    size_t reference_count, reference_capacity;
    size_t root_count, slot_count, scratch_capacity;
    size_t bytes, peak_bytes, maximum_bytes;
    const physicality_descriptor_cancel_t* cancellation;
};

static inline int plan_refusal(physicality_descriptor_plan_diagnostics_t* diagnostics,
    physicality_descriptor_plan_allocation_t allocation,
    physicality_descriptor_plan_refusal_t reason, size_t requested) {
    if (diagnostics != NULL) {
        diagnostics->allocation = allocation;
        diagnostics->refusal = reason;
        diagnostics->requested_bytes = requested;
    }
    return 0;
}

static inline void plan_snapshot(const physicality_descriptor_plan_t* plan,
    size_t completed_inputs, physicality_descriptor_plan_diagnostics_t* diagnostics) {
    if (diagnostics == NULL || plan == NULL) return;
    diagnostics->completed_inputs = completed_inputs;
    diagnostics->retained_bytes = plan->bytes;
    diagnostics->peak_bytes = plan->peak_bytes;
    diagnostics->node_count = plan->node_count;
    diagnostics->node_capacity = plan->node_capacity;
    diagnostics->child_count = plan->child_count;
    diagnostics->child_capacity = plan->child_capacity;
    diagnostics->reference_count = plan->reference_count;
    diagnostics->reference_capacity = plan->reference_capacity;
    diagnostics->root_count = plan->root_count;
    diagnostics->slot_count = plan->slot_count;
    diagnostics->scratch_capacity = plan->scratch_capacity;
}

static inline int checked_add(size_t* value, size_t addition) {
    if (addition > SIZE_MAX - *value) return 0;
    *value += addition;
    return 1;
}

static inline int checked_array(size_t* bytes, size_t count, size_t width) {
    if (count > SIZE_MAX / width) return 0;
    return checked_add(bytes, count * width);
}

static inline int capacity_bound(size_t required, size_t* out) {
    size_t capacity = required == 0u ? 0u : 1u;
    while (capacity < required) {
        if (capacity > SIZE_MAX / 2u) return 0;
        capacity *= 2u;
    }
    *out = capacity;
    return 1;
}

static inline size_t identity_slot(const hash128_t* id, size_t mask) {
    uint64_t value = id->lo ^ id->hi;
    value ^= value >> 33;
    value *= UINT64_C(0xff51afd7ed558ccd);
    value ^= value >> 33;
    return (size_t)value & mask;
}

physicality_descriptor_status_t laplace_phys_desc_describe_one(
    physicality_descriptor_plan_t* plan,
    physicality_descriptor_plan_diagnostics_t* diagnostics,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_input_t* input,
    size_t input_index);

#endif
