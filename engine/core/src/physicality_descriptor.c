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
};

static int checked_add(size_t* value, size_t addition) {
    if (addition > SIZE_MAX - *value) return 0;
    *value += addition;
    return 1;
}

static int checked_array(size_t* bytes, size_t count, size_t width) {
    if (count > SIZE_MAX / width) return 0;
    return checked_add(bytes, count * width);
}

/* Grow retained arrays under the same aggregate grant. Reserve old plus new
 * requested payload even when realloc grows in place; this conservative peak
 * is not allocator residency/RSS. Only unique descriptor structure asks for
 * node/edge growth; root/reference occurrence storage is never deduplicated. */
static int reserve_array(physicality_descriptor_plan_t* plan, void* previous,
    size_t used, size_t capacity, size_t required, size_t width,
    void** replacement, size_t* replacement_capacity) {
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
        return 0;
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
    if (allocated > plan->maximum_bytes - plan->bytes) return 0;
    memory = realloc(previous, allocated);
    if (memory == NULL) return 0;
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

static int reserve_slots(physicality_descriptor_plan_t* plan, size_t nodes) {
    size_t next = plan->slot_count, bytes, peak;
    size_t* slots;
    if (nodes <= next / 2u) return 1;
    if (nodes > SIZE_MAX / 2u) return 0;
    while (next < nodes * 2u) {
        if (next > SIZE_MAX / 2u) return 0;
        next *= 2u;
    }
    if (next > SIZE_MAX / sizeof(*slots)) return 0;
    bytes = next * sizeof(*slots);
    if (bytes > plan->maximum_bytes - plan->bytes) return 0;
    slots = calloc(next, sizeof(*slots));
    if (slots == NULL) return 0;
    for (size_t i = 0u; i < plan->node_count; ++i) {
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
    physicality_descriptor_plan_t* plan, const hash128_t* children,
    size_t count, hash128_t* result) {
    size_t expanded;
    size_t slot;
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
    if (plan->node_count == SIZE_MAX || count > SIZE_MAX - plan->child_count)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    void* replacement;
    size_t capacity;
    if (!reserve_array(plan, plan->nodes, plan->node_count, plan->node_capacity,
            plan->node_count + 1u, sizeof(*plan->nodes), &replacement, &capacity))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    plan->nodes = replacement;
    plan->node_capacity = capacity;
    if (!reserve_array(plan, plan->children, plan->child_count, plan->child_capacity,
            plan->child_count + count, sizeof(*plan->children), &replacement, &capacity))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    plan->children = replacement;
    plan->child_capacity = capacity;
    if (!reserve_slots(plan, plan->node_count + 1u))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
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
    physicality_descriptor_plan_t* plan, const physicality_descriptor_basis_t* basis,
    physicality_descriptor_tag_t tag, uint64_t value, size_t width, hash128_t* result) {
    hash128_t children[9];
    children[0] = basis->tags[tag];
    for (size_t byte = 0; byte < width; ++byte)
        children[byte + 1u] = basis->byte_numbers[(value >> (byte * 8u)) & 0xffu];
    return compose(plan, children, width + 1u, result);
}

static physicality_descriptor_status_t binary64(
    physicality_descriptor_plan_t* plan, const physicality_descriptor_basis_t* basis,
    double value, hash128_t* result) {
    uint64_t word;
    memcpy(&word, &value, sizeof(word));
    return bits(plan, basis, PHYSICALITY_DESCRIPTOR_BINARY64, word, 8u, result);
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
    physicality_descriptor_plan_t* plan, const physicality_descriptor_basis_t* basis,
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
            REQUIRE(binary64(plan, basis, vertex[axis], &fields[axis + 1u]));
    } else {
        fields[0] = basis->tags[PHYSICALITY_DESCRIPTOR_CARRIER];
        fields[1] = payload.entity_id;
        REQUIRE(reference(plan, &payload.entity_id, input_index, vertex_index,
            PHYSICALITY_DESCRIPTOR_CARRIER_ENTITY));
        REQUIRE(bits(plan, basis, PHYSICALITY_DESCRIPTOR_U16, payload.ordinal, 2u, &fields[2]));
        REQUIRE(bits(plan, basis, PHYSICALITY_DESCRIPTOR_U16, payload.run_length, 2u, &fields[3]));
        REQUIRE(bits(plan, basis, PHYSICALITY_DESCRIPTOR_U64, payload.flags, 8u, &fields[4]));
    }
    return compose(plan, fields, 5u, result);
}

static physicality_descriptor_status_t describe_one(
    physicality_descriptor_plan_t* plan, const physicality_descriptor_basis_t* basis,
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
    REQUIRE(bits(plan, basis, PHYSICALITY_DESCRIPTOR_I16, (uint16_t)input->type, 2u, &fields[2]));
    coordinate[0] = basis->tags[PHYSICALITY_DESCRIPTOR_COORDINATE];
    for (size_t axis = 0; axis < 4u; ++axis)
        REQUIRE(binary64(plan, basis, input->coord[axis], &coordinate[axis + 1u]));
    REQUIRE(compose(plan, coordinate, 5u, &fields[3]));
    hilbert[0] = basis->tags[PHYSICALITY_DESCRIPTOR_HILBERT];
    for (size_t byte = 0; byte < 16u; ++byte)
        hilbert[byte + 1u] = basis->byte_numbers[input->hilbert_index.bytes[byte]];
    REQUIRE(compose(plan, hilbert, 17u, &fields[4]));

    plan->trajectory_children[0] = basis->tags[PHYSICALITY_DESCRIPTOR_TRAJECTORY];
    if (input->trajectory_vertices == 0u) {
        plan->trajectory_children[1] = basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT];
        REQUIRE(compose(plan, plan->trajectory_children, 2u, &fields[5]));
    } else {
        for (size_t vertex = 0; vertex < input->trajectory_vertices; ++vertex)
            REQUIRE(describe_carrier(plan, basis, input->trajectory_xyzm + vertex * 4u,
                input_index, vertex, &plan->trajectory_children[vertex + 1u]));
        REQUIRE(compose(plan, plan->trajectory_children,
            input->trajectory_vertices + 1u, &fields[5]));
    }
    REQUIRE(bits(plan, basis, PHYSICALITY_DESCRIPTOR_I32,
        (uint32_t)input->n_constituents, 4u, &fields[6]));
    optional[0] = basis->tags[PHYSICALITY_DESCRIPTOR_PRESENT];
    fields[7] = basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT];
    if (!input->alignment_residual_is_null) {
        REQUIRE(binary64(plan, basis, input->alignment_residual, &optional[1]));
        REQUIRE(compose(plan, optional, 2u, &fields[7]));
    }
    fields[8] = basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT];
    if (!input->source_dim_is_null) {
        REQUIRE(bits(plan, basis, PHYSICALITY_DESCRIPTOR_I32,
            (uint32_t)input->source_dim, 4u, &optional[1]));
        REQUIRE(compose(plan, optional, 2u, &fields[8]));
    }
    return compose(plan, fields, 9u, &plan->roots[input_index]);
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
    size_t vertices = 0u, widest = 17u;
    size_t references = input_count, bytes = sizeof(physicality_descriptor_plan_t);
    physicality_descriptor_plan_t* plan;
    if (out_plan == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_plan = NULL;
    if ((input_count != 0u && inputs == NULL) ||
        !physicality_descriptor_basis_is_valid(basis) || limits == NULL)
        return PHYSICALITY_DESCRIPTOR_INVALID;
    if (input_count > SIZE_MAX / sizeof(*inputs))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    for (size_t input = 0; input < input_count; ++input) {
        size_t width = inputs[input].trajectory_vertices;
        if (!checked_add(&vertices, width) || width > SIZE_MAX / (4u * sizeof(double)) ||
            width == SIZE_MAX)
            return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
        if (width + 1u > widest) widest = width + 1u;
    }
    /* Roots and reference occurrences retain their exact input multiplicity.
     * Descriptor graph arrays grow only as distinct ordered nodes are found. */
    if (!checked_add(&references, vertices) ||
        !checked_array(&bytes, input_count, sizeof(hash128_t)) ||
        !checked_array(&bytes, references, sizeof(physicality_descriptor_reference_t)) ||
        !checked_array(&bytes, 1u, sizeof(size_t)) ||
        !checked_array(&bytes, widest, 4u * sizeof(double)) ||
        !checked_array(&bytes, widest, sizeof(hash128_t)) || bytes > limits->maximum_plan_bytes)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    plan = calloc(1u, sizeof(*plan));
    if (plan == NULL) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    if (input_count != 0u) plan->roots = calloc(input_count, sizeof(*plan->roots));
    if (references != 0u) plan->references = calloc(references, sizeof(*plan->references));
    plan->slots = calloc(1u, sizeof(*plan->slots));
    plan->scratch = calloc(widest, 4u * sizeof(double));
    plan->trajectory_children = calloc(widest, sizeof(hash128_t));
    if ((input_count != 0u && !plan->roots) || (references != 0u && !plan->references) ||
        !plan->slots || !plan->scratch || !plan->trajectory_children) {
        physicality_descriptor_plan_free(plan);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    plan->reference_capacity = references;
    plan->root_count = input_count;
    plan->slot_count = 1u;
    plan->scratch_capacity = widest;
    plan->bytes = plan->peak_bytes = bytes;
    plan->maximum_bytes = limits->maximum_plan_bytes;
    for (size_t input = 0; input < input_count; ++input) {
        physicality_descriptor_status_t status = describe_one(plan, basis, &inputs[input], input);
        if (status != PHYSICALITY_DESCRIPTOR_OK) {
            physicality_descriptor_plan_free(plan);
            return status;
        }
    }
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
