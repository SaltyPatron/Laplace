#include "laplace/core/physicality_descriptor.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/mantissa.h"

struct physicality_descriptor_readback {
    physicality_descriptor_input_t* inputs;
    double* trajectories;
    size_t count;
    size_t bytes;
    size_t peak_bytes;
};

typedef struct {
    const physicality_descriptor_node_t* nodes;
    const hash128_t* children;
    const physicality_descriptor_basis_t* basis;
    size_t* slots;
    size_t mask;
} catalog_t;

static size_t slot_for(const hash128_t* id, size_t mask) {
    uint64_t value = id->lo ^ id->hi;
    value ^= value >> 33;
    value *= UINT64_C(0xff51afd7ed558ccd);
    value ^= value >> 33;
    return (size_t)value & mask;
}

static const physicality_descriptor_node_t* find_node(const catalog_t* catalog, const hash128_t* id) {
    size_t slot = slot_for(id, catalog->mask);
    while (catalog->slots[slot] != 0u) {
        const physicality_descriptor_node_t* node = &catalog->nodes[catalog->slots[slot] - 1u];
        if (hash128_equals(&node->id, id)) return node;
        slot = (slot + 1u) & catalog->mask;
    }
    return NULL;
}

static const hash128_t* typed(const catalog_t* catalog, const hash128_t* id,
    physicality_descriptor_tag_t tag, size_t width) {
    const physicality_descriptor_node_t* node = find_node(catalog, id);
    if (node == NULL || node->child_count != width ||
        !hash128_equals(&catalog->children[node->first_child], &catalog->basis->tags[tag]))
        return NULL;
    return catalog->children + node->first_child;
}

static int byte_value(const physicality_descriptor_basis_t* basis, const hash128_t* id, uint8_t* out) {
    for (size_t value = 0; value < 256u; ++value) {
        if (hash128_equals(id, &basis->byte_numbers[value])) {
            *out = (uint8_t)value;
            return 1;
        }
    }
    return 0;
}

static int word(const catalog_t* catalog, const hash128_t* id,
    physicality_descriptor_tag_t tag, size_t width, uint64_t* out) {
    const hash128_t* fields = typed(catalog, id, tag, width + 1u);
    uint64_t value = 0u;
    if (fields == NULL) return 0;
    for (size_t i = 0; i < width; ++i) {
        uint8_t byte;
        if (!byte_value(catalog->basis, &fields[i + 1u], &byte)) return 0;
        value |= (uint64_t)byte << (i * 8u);
    }
    *out = value;
    return 1;
}

static int binary64(const catalog_t* catalog, const hash128_t* id, double* out) {
    uint64_t value;
    if (!word(catalog, id, PHYSICALITY_DESCRIPTOR_BINARY64, 8u, &value)) return 0;
    memcpy(out, &value, sizeof(value));
    return 1;
}

static int trajectory_width(const catalog_t* catalog, const hash128_t* root,
    const hash128_t** out_fields, size_t* width) {
    const hash128_t* fields = typed(catalog, root, PHYSICALITY_DESCRIPTOR_SCHEMA, 9u);
    const physicality_descriptor_node_t* trajectory;
    if (fields == NULL) return 0;
    trajectory = find_node(catalog, &fields[5]);
    if (trajectory == NULL || trajectory->child_count < 2u ||
        !hash128_equals(&catalog->children[trajectory->first_child],
            &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_TRAJECTORY])) return 0;
    *out_fields = catalog->children + trajectory->first_child + 1u;
    *width = trajectory->child_count - 1u;
    if (*width == 1u && hash128_equals(*out_fields,
        &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT])) *width = 0u;
    return 1;
}

static int decode_one(const catalog_t* catalog, const hash128_t* root,
    physicality_descriptor_input_t* input, double* trajectory) {
    const hash128_t* fields = typed(catalog, root, PHYSICALITY_DESCRIPTOR_SCHEMA, 9u);
    const hash128_t* sub;
    const hash128_t* carriers;
    size_t width;
    uint64_t value;
    if (fields == NULL || !trajectory_width(catalog, root, &carriers, &width)) return 0;
    input->entity_id = fields[1];
    if (!word(catalog, &fields[2], PHYSICALITY_DESCRIPTOR_I16, 2u, &value) || value > INT16_MAX)
        return 0;
    input->type = (int16_t)value;
    sub = typed(catalog, &fields[3], PHYSICALITY_DESCRIPTOR_COORDINATE, 5u);
    if (sub == NULL) return 0;
    for (size_t axis = 0; axis < 4u; ++axis)
        if (!binary64(catalog, &sub[axis + 1u], &input->coord[axis])) return 0;
    sub = typed(catalog, &fields[4], PHYSICALITY_DESCRIPTOR_HILBERT, 17u);
    if (sub == NULL) return 0;
    for (size_t byte = 0; byte < 16u; ++byte)
        if (!byte_value(catalog->basis, &sub[byte + 1u], &input->hilbert_index.bytes[byte])) return 0;
    for (size_t i = 0; i < width; ++i) {
        sub = typed(catalog, &carriers[i], PHYSICALITY_DESCRIPTOR_CARRIER, 5u);
        if (sub != NULL) {
            mantissa_payload_t payload;
            payload.entity_id = sub[1];
            if (!word(catalog, &sub[2], PHYSICALITY_DESCRIPTOR_U16, 2u, &value)) return 0;
            payload.ordinal = (uint16_t)value;
            if (!word(catalog, &sub[3], PHYSICALITY_DESCRIPTOR_U16, 2u, &value)) return 0;
            payload.run_length = (uint16_t)value;
            if (!word(catalog, &sub[4], PHYSICALITY_DESCRIPTOR_U64, 8u, &payload.flags) ||
                payload.flags > UINT64_C(0xfffffffffffff)) return 0;
            mantissa_pack(trajectory + i * 4u, &payload);
        } else {
            sub = typed(catalog, &carriers[i], PHYSICALITY_DESCRIPTOR_FACTOR, 5u);
            if (sub == NULL) return 0;
            for (size_t axis = 0; axis < 4u; ++axis)
                if (!binary64(catalog, &sub[axis + 1u], &trajectory[i * 4u + axis])) return 0;
        }
    }
    input->trajectory_vertices = width;
    input->trajectory_xyzm = width == 0u ? NULL : trajectory;
    if (!word(catalog, &fields[6], PHYSICALITY_DESCRIPTOR_I32, 4u, &value) || value > INT32_MAX)
        return 0;
    input->n_constituents = (int32_t)value;
    input->alignment_residual_is_null = hash128_equals(
        &fields[7], &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT]);
    if (!input->alignment_residual_is_null) {
        sub = typed(catalog, &fields[7], PHYSICALITY_DESCRIPTOR_PRESENT, 2u);
        if (sub == NULL || !binary64(catalog, &sub[1], &input->alignment_residual)) return 0;
    }
    input->source_dim_is_null = hash128_equals(
        &fields[8], &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT]);
    if (!input->source_dim_is_null) {
        sub = typed(catalog, &fields[8], PHYSICALITY_DESCRIPTOR_PRESENT, 2u);
        if (sub == NULL || !word(catalog, &sub[1], PHYSICALITY_DESCRIPTOR_I32, 4u, &value) ||
            value > INT32_MAX) return 0;
        input->source_dim = (int32_t)value;
    }
    return 1;
}

void physicality_descriptor_readback_free(physicality_descriptor_readback_t* readback) {
    if (readback == NULL) return;
    free(readback->inputs);
    free(readback->trajectories);
    free(readback);
}

static int array_bytes(size_t* bytes, size_t count, size_t width) {
    if (count > (SIZE_MAX - *bytes) / width) return 0;
    *bytes += count * width;
    return 1;
}

physicality_descriptor_status_t physicality_descriptor_readback_build(
    const physicality_descriptor_node_t* nodes, size_t node_count,
    const hash128_t* children, size_t child_count,
    const hash128_t* roots, size_t root_count,
    const physicality_descriptor_basis_t* basis,
    const physicality_descriptor_limits_t* plan_limits,
    size_t maximum_readback_bytes, physicality_descriptor_readback_t** out_readback) {
    size_t slots = 1u, bytes = sizeof(physicality_descriptor_readback_t), vertices = 0u;
    catalog_t catalog = {nodes, children, basis, NULL, 0u};
    physicality_descriptor_readback_t* readback = NULL;
    physicality_descriptor_plan_t* verified_plan = NULL;
    physicality_descriptor_limits_t remaining_limits;
    physicality_descriptor_status_t status = PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    if (out_readback == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_readback = NULL;
    if ((node_count != 0u && nodes == NULL) || (child_count != 0u && children == NULL) ||
        (root_count != 0u && roots == NULL) ||
        !physicality_descriptor_basis_is_valid(basis) || plan_limits == NULL)
        return PHYSICALITY_DESCRIPTOR_INVALID;
    if (node_count > SIZE_MAX / sizeof(*nodes) || child_count > SIZE_MAX / sizeof(*children) ||
        root_count > SIZE_MAX / sizeof(*roots)) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    if (node_count > SIZE_MAX / 2u) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    while (slots < node_count * 2u) {
        if (slots > SIZE_MAX / 2u) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
        slots *= 2u;
    }
    if (!array_bytes(&bytes, slots, sizeof(size_t)) ||
        !array_bytes(&bytes, root_count, sizeof(physicality_descriptor_input_t)) ||
        bytes > maximum_readback_bytes) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    catalog.slots = calloc(slots, sizeof(size_t));
    if (catalog.slots == NULL) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    catalog.mask = slots - 1u;
    for (size_t i = 0; i < node_count; ++i) {
        hash128_t actual;
        size_t slot;
        const physicality_descriptor_node_t* node = &nodes[i];
        if (node->child_count < 2u || node->first_child > child_count ||
            node->child_count > child_count - node->first_child) goto done;
        hash128_merkle(0u, children + node->first_child, node->child_count, &actual);
        if (!hash128_equals(&actual, &node->id)) goto done;
        slot = slot_for(&node->id, catalog.mask);
        while (catalog.slots[slot] != 0u) {
            const physicality_descriptor_node_t* old = &nodes[catalog.slots[slot] - 1u];
            if (hash128_equals(&old->id, &node->id)) {
                if (old->child_count != node->child_count || memcmp(children + old->first_child,
                    children + node->first_child, node->child_count * sizeof(hash128_t)) != 0) goto done;
                break;
            }
            slot = (slot + 1u) & catalog.mask;
        }
        catalog.slots[slot] = i + 1u;
    }
    for (size_t i = 0; i < root_count; ++i) {
        const hash128_t* fields;
        size_t width;
        if (!trajectory_width(&catalog, &roots[i], &fields, &width)) goto done;
        if (!array_bytes(&vertices, width, 1u)) {
            status = PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
            goto done;
        }
    }
    if (!array_bytes(&bytes, vertices, 4u * sizeof(double)) || bytes > maximum_readback_bytes) {
        status = PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
        goto done;
    }
    readback = calloc(1u, sizeof(*readback));
    if (readback == NULL) {
        status = PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
        goto done;
    }
    if (root_count != 0u) readback->inputs = calloc(root_count, sizeof(*readback->inputs));
    if (vertices != 0u) readback->trajectories = calloc(vertices, 4u * sizeof(double));
    if ((root_count != 0u && readback->inputs == NULL) || (vertices != 0u && readback->trajectories == NULL)) {
        status = PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
        goto done;
    }
    readback->bytes = bytes - slots * sizeof(size_t);
    readback->count = root_count;
    vertices = 0u;
    for (size_t i = 0; i < root_count; ++i) {
        if (!decode_one(&catalog, &roots[i], &readback->inputs[i], readback->trajectories == NULL ? NULL :
            readback->trajectories + vertices * 4u)) goto done;
        vertices += readback->inputs[i].trajectory_vertices;
    }
    remaining_limits = *plan_limits;
    if (remaining_limits.maximum_plan_bytes > maximum_readback_bytes - bytes)
        remaining_limits.maximum_plan_bytes = maximum_readback_bytes - bytes;
    status = physicality_descriptor_plan_build(readback->inputs, root_count, basis, &remaining_limits,
        &verified_plan);
    if (status == PHYSICALITY_DESCRIPTOR_OK) {
        const hash128_t* verified = physicality_descriptor_plan_roots(verified_plan, NULL);
        readback->peak_bytes = bytes + physicality_descriptor_plan_bytes(verified_plan);
        for (size_t i = 0; i < root_count; ++i) {
            if (!hash128_equals(&verified[i], &roots[i])) {
                status = PHYSICALITY_DESCRIPTOR_INVALID_BODY;
                goto done;
            }
        }
        *out_readback = readback;
        readback = NULL;
    }
done:
    physicality_descriptor_plan_free(verified_plan);
    free(catalog.slots);
    physicality_descriptor_readback_free(readback);
    return status;
}

size_t physicality_descriptor_readback_bytes(const physicality_descriptor_readback_t* readback) {
    return readback == NULL ? 0u : readback->bytes;
}

size_t physicality_descriptor_readback_peak_bytes(const physicality_descriptor_readback_t* readback) {
    return readback == NULL ? 0u : readback->peak_bytes;
}

const physicality_descriptor_input_t* physicality_descriptor_readback_inputs(
    const physicality_descriptor_readback_t* readback, size_t* count) {
    if (count != NULL) *count = readback == NULL ? 0u : readback->count;
    return readback == NULL ? NULL : readback->inputs;
}
