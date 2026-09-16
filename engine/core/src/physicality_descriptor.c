#include "laplace/core/physicality_descriptor.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "laplace/core/mantissa.h"
#include "laplace/core/trajectory.h"

struct physicality_descriptor_plan {
    physicality_descriptor_node_t* nodes;
    hash128_t* children;
    hash128_t* roots;
    physicality_descriptor_reference_t* references;
    size_t* slots;
    double* scratch;
    hash128_t* trajectory_children;
    size_t node_count, node_capacity;
    size_t child_count, child_capacity;
    size_t reference_count, reference_capacity;
    size_t root_count, slot_count, scratch_capacity;
    size_t bytes, peak_bytes, maximum_bytes;
    const physicality_descriptor_cancel_t* cancellation;
};

static int plan_refusal(physicality_descriptor_plan_diagnostics_t* diagnostics,
    physicality_descriptor_plan_allocation_t allocation,
    physicality_descriptor_plan_refusal_t reason, size_t requested) {
    if (diagnostics != NULL) {
        diagnostics->allocation = allocation;
        diagnostics->refusal = reason;
        diagnostics->requested_bytes = requested;
    }
    return 0;
}

static void plan_snapshot(const physicality_descriptor_plan_t* plan,
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

static int checked_add(size_t* value, size_t addition) {
    if (addition > SIZE_MAX - *value) return 0;
    *value += addition;
    return 1;
}

static int checked_array(size_t* bytes, size_t count, size_t width) {
    if (count > SIZE_MAX / width) return 0;
    return checked_add(bytes, count * width);
}

/* The same doubling law as graph growth, with no allocation. */
static int capacity_bound(size_t required, size_t* out) {
    size_t capacity = required == 0u ? 0u : 1u;
    while (capacity < required) {
        if (capacity > SIZE_MAX / 2u) return 0;
        capacity *= 2u;
    }
    *out = capacity;
    return 1;
}

physicality_descriptor_status_t physicality_descriptor_plan_payload_bound(
    size_t forms, size_t stored_vertices, size_t maximum_vertices,
    size_t* out_peak_bytes) {
    size_t nodes = 0u, children = 0u, references = forms;
    size_t node_capacity, child_capacity, slot_capacity, slots_required;
    size_t node_bytes = 0u, child_bytes = 0u, slot_bytes = 0u;
    size_t bytes = sizeof(physicality_descriptor_plan_t), widest = 17u, replacement;
    if (out_peak_bytes == NULL || (forms == 0u && stored_vertices != 0u) ||
        maximum_vertices > stored_vertices ||
        (stored_vertices != 0u && maximum_vertices == 0u))
        return PHYSICALITY_DESCRIPTOR_INVALID;
    /* maximum_vertices must be capable of covering the total row shape.
     * Division avoids overflowing forms * maximum_vertices. */
    if (stored_vertices != 0u && (stored_vertices - 1u) / forms >= maximum_vertices)
        return PHYSICALITY_DESCRIPTOR_INVALID;
    if (maximum_vertices == SIZE_MAX || forms > SIZE_MAX / sizeof(physicality_descriptor_input_t))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    if (maximum_vertices + 1u > widest) widest = maximum_vertices + 1u;
    /* Every optional scalar is present and every vertex takes the larger
     * factor recipe. Realized/carrier references keep all occurrences. */
    if (!checked_array(&nodes, forms, 14u) || !checked_array(&nodes, stored_vertices, 5u) ||
        !checked_array(&children, forms, 95u) || !checked_array(&children, stored_vertices, 42u) ||
        !checked_add(&references, stored_vertices) ||
        !checked_array(&bytes, forms, sizeof(hash128_t)) ||
        !checked_array(&bytes, references, sizeof(physicality_descriptor_reference_t)) ||
        !checked_array(&bytes, widest, 4u * sizeof(double)) ||
        !checked_array(&bytes, widest, sizeof(hash128_t)) ||
        !capacity_bound(nodes, &node_capacity) || !capacity_bound(children, &child_capacity) ||
        nodes > SIZE_MAX / 2u)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    slots_required = nodes == 0u ? 1u : nodes * 2u;
    if (!capacity_bound(slots_required, &slot_capacity) ||
        !checked_array(&node_bytes, node_capacity, sizeof(physicality_descriptor_node_t)) ||
        !checked_array(&child_bytes, child_capacity, sizeof(hash128_t)) ||
        !checked_array(&slot_bytes, slot_capacity, sizeof(size_t)) ||
        !checked_add(&bytes, node_bytes) || !checked_add(&bytes, child_bytes) ||
        !checked_add(&bytes, slot_bytes))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    replacement = node_bytes > child_bytes ? node_bytes : child_bytes;
    if (slot_bytes > replacement) replacement = slot_bytes;
    /* Empty plans never replace an array. Otherwise this retains the complete
     * final capacities alongside the largest possible replacement request. */
    if (nodes != 0u && !checked_add(&bytes, replacement))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    *out_peak_bytes = bytes;
    return PHYSICALITY_DESCRIPTOR_OK;
}

/* Grow retained arrays under the same aggregate grant. Reserve old plus new
 * requested payload even when realloc grows in place; this conservative peak
 * is not allocator residency/RSS. Only unique descriptor structure asks for
 * node/edge growth; root/reference occurrence storage is never deduplicated. */
static int reserve_array(physicality_descriptor_plan_t* plan, void* previous,
    size_t used, size_t capacity, size_t required, size_t width,
    physicality_descriptor_plan_diagnostics_t* diagnostics,
    physicality_descriptor_plan_allocation_t allocation, void** replacement, size_t* replacement_capacity) {
    size_t next = capacity == 0u ? 1u : capacity;
    size_t allocated, previous_bytes;
    void* memory;
    if (required <= capacity) {
        *replacement = previous;
        *replacement_capacity = capacity;
        return 1;
    }
    if (required > SIZE_MAX / width || capacity > SIZE_MAX / width ||
        used > capacity || plan->bytes > plan->maximum_bytes)
        return plan_refusal(diagnostics, allocation, PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
    while (next < required) {
        if (next > SIZE_MAX / 2u) { next = required; break; }
        next *= 2u;
    }
    /* Geometric spare capacity is optional; a valid exact fit may use only
     * the required rows, still charging old plus new during replacement. */
    if (next > SIZE_MAX / width ||
        next * width > plan->maximum_bytes - plan->bytes)
        next = required;
    allocated = next * width;
    if (allocated > plan->maximum_bytes - plan->bytes)
        return plan_refusal(diagnostics, allocation, PHYSICALITY_DESCRIPTOR_PLAN_GRANT_REFUSED, allocated);
    memory = realloc(previous, allocated);
    if (memory == NULL)
        return plan_refusal(diagnostics, allocation, PHYSICALITY_DESCRIPTOR_PLAN_ALLOCATOR_REFUSED, allocated);
    /* Preserve calloc's complete zero tail, including unused old capacity. */
    memset((uint8_t*)memory + used * width, 0, allocated - used * width);
    if (plan->bytes + allocated > plan->peak_bytes)
        plan->peak_bytes = plan->bytes + allocated;
    previous_bytes = capacity * width;
    plan->bytes += allocated - previous_bytes;
    *replacement = memory;
    *replacement_capacity = next;
    return 1;
}

static size_t identity_slot(const hash128_t* id, size_t mask) {
    /* Hash-table routing only. Canonical identities come from the ordinary
     * native trajectory/composition owner below. */
    uint64_t value = id->lo ^ id->hi;
    value ^= value >> 33;
    value *= UINT64_C(0xff51afd7ed558ccd);
    value ^= value >> 33;
    return (size_t)value & mask;
}

static int reserve_slots(physicality_descriptor_plan_t* plan, size_t nodes,
    physicality_descriptor_plan_diagnostics_t* diagnostics) {
    size_t next = plan->slot_count, bytes, peak;
    size_t* slots;
    if (nodes <= next / 2u) return 1;
    if (nodes > SIZE_MAX / 2u)
        return plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_SLOTS,
            PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
    while (next < nodes * 2u) {
        if (next > SIZE_MAX / 2u)
            return plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_SLOTS,
                PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
        next *= 2u;
    }
    if (next > SIZE_MAX / sizeof(*slots))
        return plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_SLOTS,
            PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
    bytes = next * sizeof(*slots);
    if (bytes > plan->maximum_bytes - plan->bytes)
        return plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_SLOTS,
            PHYSICALITY_DESCRIPTOR_PLAN_GRANT_REFUSED, bytes);
    slots = calloc(next, sizeof(*slots));
    if (slots == NULL)
        return plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_SLOTS,
            PHYSICALITY_DESCRIPTOR_PLAN_ALLOCATOR_REFUSED, bytes);
    for (size_t i = 0u; i < plan->node_count; ++i) {
        if (physicality_descriptor_cancel_requested(plan->cancellation)) {
            free(slots);
            return -1;
        }
        size_t slot = identity_slot(&plan->nodes[i].id, next - 1u);
        while (slots[slot] != 0u) slot = (slot + 1u) & (next - 1u);
        slots[slot] = i + 1u;
    }
    peak = plan->bytes + bytes;
    if (peak > plan->peak_bytes) plan->peak_bytes = peak;
    free(plan->slots);
    plan->bytes += bytes - plan->slot_count * sizeof(*slots);
    plan->slots = slots;
    plan->slot_count = next;
    return 1;
}

static const hash128_t* basis_value(const physicality_descriptor_basis_t* basis, size_t index) {
    return index < PHYSICALITY_DESCRIPTOR_TAG_COUNT ? &basis->tags[index] :
        &basis->byte_numbers[index - PHYSICALITY_DESCRIPTOR_TAG_COUNT];
}

int physicality_descriptor_basis_is_valid(const physicality_descriptor_basis_t* basis) {
    uint16_t slots[1024] = {0};
    if (basis == NULL) return 0;
    for (size_t i = 0; i < PHYSICALITY_DESCRIPTOR_TAG_COUNT + 256u; ++i) {
        const hash128_t* id = basis_value(basis, i);
        size_t slot = identity_slot(id, 1023u);
        while (slots[slot] != 0u) {
            if (hash128_equals(id, basis_value(basis, slots[slot] - 1u))) return 0;
            slot = (slot + 1u) & 1023u;
        }
        slots[slot] = (uint16_t)(i + 1u);
    }
    return 1;
}

static physicality_descriptor_status_t compose(
    physicality_descriptor_plan_t* plan, physicality_descriptor_plan_diagnostics_t* diagnostics, const hash128_t* children,
    size_t count, hash128_t* result) {
    size_t expanded;
    size_t slot;
    if (physicality_descriptor_cancel_requested(plan->cancellation))
        return PHYSICALITY_DESCRIPTOR_CANCELLED;
    if (count < 2u || count > plan->scratch_capacity ||
        trajectory_build(children, count, plan->scratch) != 0 ||
        trajectory_content_identity(plan->scratch, count, result, &expanded) != 0 ||
        expanded != count)
        return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    slot = identity_slot(result, plan->slot_count - 1u);
    while (plan->slots[slot] != 0u) {
        const physicality_descriptor_node_t* node = &plan->nodes[plan->slots[slot] - 1u];
        if (hash128_equals(&node->id, result)) {
            if (node->child_count != count ||
                memcmp(plan->children + node->first_child, children,
                    count * sizeof(*children)) != 0)
                return PHYSICALITY_DESCRIPTOR_IDENTITY_CONFLICT;
            return PHYSICALITY_DESCRIPTOR_OK;
        }
        slot = (slot + 1u) & (plan->slot_count - 1u);
    }
    /* Exact reuse above needs no spare capacity. Growth cannot invalidate
     * children/result: callers use stack fields or the fixed scratch/root
     * arrays, never the growable node/child arrays. */
    if (plan->node_count == SIZE_MAX || count > SIZE_MAX - plan->child_count) {
        plan_refusal(diagnostics, PHYSICALITY_DESCRIPTOR_PLAN_GRAPH,
            PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW, 0u);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    void* replacement;
    size_t capacity;
    if (!reserve_array(plan, plan->nodes, plan->node_count, plan->node_capacity,
            plan->node_count + 1u, sizeof(*plan->nodes), diagnostics,
            PHYSICALITY_DESCRIPTOR_PLAN_NODES, &replacement, &capacity))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    plan->nodes = replacement;
    plan->node_capacity = capacity;
    if (!reserve_array(plan, plan->children, plan->child_count, plan->child_capacity,
            plan->child_count + count, sizeof(*plan->children), diagnostics,
            PHYSICALITY_DESCRIPTOR_PLAN_CHILDREN, &replacement, &capacity))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    plan->children = replacement;
    plan->child_capacity = capacity;
    const int slots_status = reserve_slots(plan, plan->node_count + 1u, diagnostics);
    if (slots_status != 1)
        return slots_status < 0 ? PHYSICALITY_DESCRIPTOR_CANCELLED : PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    slot = identity_slot(result, plan->slot_count - 1u);
    while (plan->slots[slot] != 0u)
        slot = (slot + 1u) & (plan->slot_count - 1u);
    physicality_descriptor_node_t* node = &plan->nodes[plan->node_count];
    node->id = *result;
    node->first_child = plan->child_count;
    node->child_count = count;
    memcpy(plan->children + plan->child_count, children, count * sizeof(*children));
    plan->child_count += count;
    plan->slots[slot] = ++plan->node_count;
    return PHYSICALITY_DESCRIPTOR_OK;
}

static physicality_descriptor_status_t bits(
    physicality_descriptor_plan_t* plan, physicality_descriptor_plan_diagnostics_t* diagnostics, const physicality_descriptor_basis_t* basis,
    physicality_descriptor_tag_t tag, uint64_t value, size_t width, hash128_t* result) {
    hash128_t children[9];
    children[0] = basis->tags[tag];
    for (size_t byte = 0; byte < width; ++byte)
        children[byte + 1u] = basis->byte_numbers[(value >> (byte * 8u)) & 0xffu];
    return compose(plan, diagnostics, children, width + 1u, result);
}

static physicality_descriptor_status_t binary64(
    physicality_descriptor_plan_t* plan, physicality_descriptor_plan_diagnostics_t* diagnostics, const physicality_descriptor_basis_t* basis,
    double value, hash128_t* result) {
    uint64_t word;
    memcpy(&word, &value, sizeof(word));
    return bits(plan, diagnostics, basis, PHYSICALITY_DESCRIPTOR_BINARY64, word, 8u, result);
}

static physicality_descriptor_status_t reference(
    physicality_descriptor_plan_t* plan, const hash128_t* entity,
    size_t input, size_t vertex, physicality_descriptor_reference_kind_t kind) {
    if (plan->reference_count == plan->reference_capacity)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    physicality_descriptor_reference_t* value = &plan->references[plan->reference_count++];
    value->entity_id = *entity;
    value->input_index = input;
    value->vertex_index = vertex;
    value->kind = kind;
    return PHYSICALITY_DESCRIPTOR_OK;
}

#define REQUIRE(expression) do { \
    physicality_descriptor_status_t status_ = (expression); \
    if (status_ != PHYSICALITY_DESCRIPTOR_OK) return status_; \
} while (0)

static physicality_descriptor_status_t describe_carrier(
    physicality_descriptor_plan_t* plan, physicality_descriptor_plan_diagnostics_t* diagnostics, const physicality_descriptor_basis_t* basis,
    const double* vertex, size_t input_index, size_t vertex_index, hash128_t* result) {
    mantissa_payload_t payload;
    double restored[4];
    hash128_t fields[5];
    mantissa_unpack(vertex, &payload);
    mantissa_pack(restored, &payload);
    if (memcmp(restored, vertex, sizeof(restored)) != 0)
        return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    if ((payload.flags & LAPLACE_VFLAG_FACTOR) != 0u &&
        (payload.flags & (LAPLACE_VFLAG_HAS_ATOM | LAPLACE_VFLAG_TESTIMONY)) == 0u) {
        /* This vertex's id-sized payload contains float32 values, not an
         * entity reference. Preserve its exact numeric representation. */
        float values[6];
        uint8_t count;
        if (laplace_factor_unpack_vertex(vertex, values, &count) != 0)
            return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
        fields[0] = basis->tags[PHYSICALITY_DESCRIPTOR_FACTOR];
        for (size_t axis = 0; axis < 4u; ++axis)
            REQUIRE(binary64(plan, diagnostics, basis, vertex[axis], &fields[axis + 1u]));
    } else {
        fields[0] = basis->tags[PHYSICALITY_DESCRIPTOR_CARRIER];
        fields[1] = payload.entity_id;
        REQUIRE(reference(plan, &payload.entity_id, input_index, vertex_index,
            PHYSICALITY_DESCRIPTOR_CARRIER_ENTITY));
        REQUIRE(bits(plan, diagnostics, basis, PHYSICALITY_DESCRIPTOR_U16, payload.ordinal, 2u, &fields[2]));
        REQUIRE(bits(plan, diagnostics, basis, PHYSICALITY_DESCRIPTOR_U16, payload.run_length, 2u, &fields[3]));
        REQUIRE(bits(plan, diagnostics, basis, PHYSICALITY_DESCRIPTOR_U64, payload.flags, 8u, &fields[4]));
    }
    return compose(plan, diagnostics, fields, 5u, result);
}

static physicality_descriptor_status_t describe_one(
    physicality_descriptor_plan_t* plan, physicality_descriptor_plan_diagnostics_t* diagnostics, const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_input_t* input, size_t input_index) {
    hash128_t fields[9];
    hash128_t coordinate[5];
    hash128_t hilbert[17];
    hash128_t optional[2];
    if (input->type <= 0 || input->n_constituents < 0 ||
        (input->trajectory_vertices != 0u && input->trajectory_xyzm == NULL) ||
        (input->trajectory_vertices == 0u && input->n_constituents != 0) ||
        (input->alignment_residual_is_null != 0 && input->alignment_residual_is_null != 1) ||
        (input->source_dim_is_null != 0 && input->source_dim_is_null != 1) ||
        (!input->source_dim_is_null && input->source_dim <= 0))
        return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    for (size_t axis = 0; axis < 4u; ++axis)
        if (!isfinite(input->coord[axis])) return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    if (!input->alignment_residual_is_null && !isfinite(input->alignment_residual))
        return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    if (laplace_physicality_manifest_validate(&input->entity_id, input->type,
        input->trajectory_xyzm, input->trajectory_vertices, input->n_constituents) != 0)
        return PHYSICALITY_DESCRIPTOR_INVALID_BODY;

    fields[0] = basis->tags[PHYSICALITY_DESCRIPTOR_SCHEMA];
    fields[1] = input->entity_id;
    REQUIRE(reference(plan, &input->entity_id, input_index, SIZE_MAX,
        PHYSICALITY_DESCRIPTOR_REALIZED_ENTITY));
    REQUIRE(bits(plan, diagnostics, basis, PHYSICALITY_DESCRIPTOR_I16, (uint16_t)input->type, 2u, &fields[2]));
    coordinate[0] = basis->tags[PHYSICALITY_DESCRIPTOR_COORDINATE];
    for (size_t axis = 0; axis < 4u; ++axis)
        REQUIRE(binary64(plan, diagnostics, basis, input->coord[axis], &coordinate[axis + 1u]));
    REQUIRE(compose(plan, diagnostics, coordinate, 5u, &fields[3]));
    hilbert[0] = basis->tags[PHYSICALITY_DESCRIPTOR_HILBERT];
    for (size_t byte = 0; byte < 16u; ++byte)
        hilbert[byte + 1u] = basis->byte_numbers[input->hilbert_index.bytes[byte]];
    REQUIRE(compose(plan, diagnostics, hilbert, 17u, &fields[4]));

    plan->trajectory_children[0] = basis->tags[PHYSICALITY_DESCRIPTOR_TRAJECTORY];
    if (input->trajectory_vertices == 0u) {
        plan->trajectory_children[1] = basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT];
        REQUIRE(compose(plan, diagnostics, plan->trajectory_children, 2u, &fields[5]));
    } else {
        for (size_t vertex = 0; vertex < input->trajectory_vertices; ++vertex)
            REQUIRE(describe_carrier(plan, diagnostics, basis, input->trajectory_xyzm + vertex * 4u,
                input_index, vertex, &plan->trajectory_children[vertex + 1u]));
        REQUIRE(compose(plan, diagnostics, plan->trajectory_children,
            input->trajectory_vertices + 1u, &fields[5]));
    }
    REQUIRE(bits(plan, diagnostics, basis, PHYSICALITY_DESCRIPTOR_I32,
        (uint32_t)input->n_constituents, 4u, &fields[6]));
    optional[0] = basis->tags[PHYSICALITY_DESCRIPTOR_PRESENT];
    fields[7] = basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT];
    if (!input->alignment_residual_is_null) {
        REQUIRE(binary64(plan, diagnostics, basis, input->alignment_residual, &optional[1]));
        REQUIRE(compose(plan, diagnostics, optional, 2u, &fields[7]));
    }
    fields[8] = basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT];
    if (!input->source_dim_is_null) {
        REQUIRE(bits(plan, diagnostics, basis, PHYSICALITY_DESCRIPTOR_I32,
            (uint32_t)input->source_dim, 4u, &optional[1]));
        REQUIRE(compose(plan, diagnostics, optional, 2u, &fields[8]));
    }
    return compose(plan, diagnostics, fields, 9u, &plan->roots[input_index]);
}

/* Invocation-local reuse of the immediately preceding validated body. Compare
 * the complete active recipe, never the placement/entity id alone, structure
 * padding, trajectory pointer identity, or inactive nullable payload. A miss
 * takes the ordinary validator/composer; a hit preserves every reference
 * occurrence below. No heap cache or plan payload/capacity change is needed. */
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

void physicality_descriptor_plan_free(physicality_descriptor_plan_t* plan) {
    if (plan == NULL) return;
    free(plan->nodes);
    free(plan->children);
    free(plan->roots);
    free(plan->references);
    free(plan->slots);
    free(plan->scratch);
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
    size_t vertices = 0u, widest = 17u;
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
    }
    /* Roots and reference occurrences retain their exact input multiplicity.
     * Descriptor graph arrays grow only as distinct ordered nodes are found. */
    if (!checked_add(&references, vertices) ||
        !checked_array(&bytes, input_count, sizeof(hash128_t)) ||
        !checked_array(&bytes, references, sizeof(physicality_descriptor_reference_t)) ||
        !checked_array(&bytes, 1u, sizeof(size_t)) ||
        !checked_array(&bytes, widest, 4u * sizeof(double)) ||
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
    plan->scratch = calloc(widest, 4u * sizeof(double));
    plan->trajectory_children = calloc(widest, sizeof(hash128_t));
    if ((input_count != 0u && !plan->roots) || (references != 0u && !plan->references) ||
        !plan->slots || !plan->scratch || !plan->trajectory_children) {
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
    size_t previous_reference_first = 0u, previous_reference_count = 0u;
    for (size_t input = 0; input < input_count; ++input) {
        const size_t reference_first = plan->reference_count;
        int equal = 0;
        physicality_descriptor_status_t status = physicality_descriptor_cancel_requested(cancellation)
            ? PHYSICALITY_DESCRIPTOR_CANCELLED : PHYSICALITY_DESCRIPTOR_OK;
        if (status == PHYSICALITY_DESCRIPTOR_OK && input != 0u)
            status = same_body(&inputs[input], &inputs[input - 1u], cancellation, &equal);
        if (status == PHYSICALITY_DESCRIPTOR_OK) {
            if (equal) {
                status = repeat_body_references(plan, previous_reference_first,
                    previous_reference_count, input);
                if (status == PHYSICALITY_DESCRIPTOR_OK)
                    plan->roots[input] = plan->roots[input - 1u];
            } else {
                status = describe_one(plan, diagnostics, basis, &inputs[input], input);
            }
        }
        if (status != PHYSICALITY_DESCRIPTOR_OK) {
            plan_snapshot(plan, input, diagnostics);
            physicality_descriptor_plan_free(plan);
            return status;
        }
        previous_reference_first = reference_first;
        previous_reference_count = plan->reference_count - reference_first;
    }
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
