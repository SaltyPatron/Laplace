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
    hash128_t* missing;
    size_t missing_count;
    size_t content_hash_operands;
};

typedef struct {
    const physicality_descriptor_node_t* nodes;
    const hash128_t* children;
    const physicality_descriptor_basis_t* basis;
    size_t* slots;
    size_t mask;
    hash128_t* missing;
    size_t* missing_slots;
    size_t missing_mask, missing_count;
    int invalid;
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

static const physicality_descriptor_node_t* require_node(catalog_t* catalog, const hash128_t* id) {
    /* A typed structural slot cannot reinterpret a declared vocabulary leaf
     * as an unresolved graph node, even in a malformed containment candidate. */
    for (size_t i = 0; i < PHYSICALITY_DESCRIPTOR_TAG_COUNT; ++i)
        if (hash128_equals(id, &catalog->basis->tags[i])) { catalog->invalid = 1; return NULL; }
    for (size_t i = 0; i < 256u; ++i)
        if (hash128_equals(id, &catalog->basis->byte_numbers[i])) { catalog->invalid = 1; return NULL; }
    const physicality_descriptor_node_t* node = find_node(catalog, id);
    if (node == NULL) {
        if (catalog->missing != NULL) {
            size_t slot = slot_for(id, catalog->missing_mask);
            while (catalog->missing_slots[slot] != 0u) {
                if (hash128_equals(id, &catalog->missing[catalog->missing_slots[slot] - 1u])) return NULL;
                slot = (slot + 1u) & catalog->missing_mask;
            }
            catalog->missing[catalog->missing_count++] = *id;
            catalog->missing_slots[slot] = catalog->missing_count;
        } else catalog->invalid = 1;
    }
    return node;
}

static const hash128_t* typed(catalog_t* catalog, const hash128_t* id,
    physicality_descriptor_tag_t tag, size_t width) {
    const physicality_descriptor_node_t* node = require_node(catalog, id);
    if (node == NULL) return NULL;
    if (node->child_count != width ||
        !hash128_equals(&catalog->children[node->first_child], &catalog->basis->tags[tag])) {
        catalog->invalid = 1;
        return NULL;
    }
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

static int word(catalog_t* catalog, const hash128_t* id,
    physicality_descriptor_tag_t tag, size_t width, uint64_t* out) {
    const hash128_t* fields = typed(catalog, id, tag, width + 1u);
    uint64_t value = 0u;
    if (fields == NULL) return 0;
    for (size_t i = 0; i < width; ++i) {
        uint8_t byte;
        if (!byte_value(catalog->basis, &fields[i + 1u], &byte)) { catalog->invalid = 1; return 0; }
        value |= (uint64_t)byte << (i * 8u);
    }
    *out = value;
    return 1;
}

static int binary64(catalog_t* catalog, const hash128_t* id, double* out) {
    uint64_t value;
    if (!word(catalog, id, PHYSICALITY_DESCRIPTOR_BINARY64, 8u, &value)) return 0;
    memcpy(out, &value, sizeof(value));
    return 1;
}

static int trajectory_width(catalog_t* catalog, const hash128_t* root,
    const hash128_t** out_fields, size_t* width) {
    const hash128_t* fields = typed(catalog, root, PHYSICALITY_DESCRIPTOR_SCHEMA, 9u);
    const physicality_descriptor_node_t* trajectory;
    if (fields == NULL) return 0;
    trajectory = require_node(catalog, &fields[5]);
    if (trajectory == NULL) return 0;
    if (trajectory->child_count < 2u ||
        !hash128_equals(&catalog->children[trajectory->first_child],
            &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_TRAJECTORY])) {
        catalog->invalid = 1; return 0;
    }
    *out_fields = catalog->children + trajectory->first_child + 1u;
    *width = trajectory->child_count - 1u;
    if (*width == 1u && hash128_equals(*out_fields,
        &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT])) *width = 0u;
    return 1;
}

/* A NULL trajectory runs the exact same typed field decoder as discovery.
 * Missing siblings are collected together; no external reference is followed. */
static int decode_one(catalog_t* catalog, const hash128_t* root,
    physicality_descriptor_input_t* input, double* trajectory) {
    const hash128_t* fields = typed(catalog, root, PHYSICALITY_DESCRIPTOR_SCHEMA, 9u);
    const hash128_t* sub;
    const hash128_t* carriers = NULL;
    size_t width = 0u;
    uint64_t value = 0u;
    if (fields == NULL) return 0;
    input->entity_id = fields[1];
    if (word(catalog, &fields[2], PHYSICALITY_DESCRIPTOR_I16, 2u, &value)) {
        if (value > INT16_MAX) catalog->invalid = 1;
        else input->type = (int16_t)value;
    }
    sub = typed(catalog, &fields[3], PHYSICALITY_DESCRIPTOR_COORDINATE, 5u);
    if (sub != NULL) for (size_t axis = 0; axis < 4u; ++axis)
        (void)binary64(catalog, &sub[axis + 1u], &input->coord[axis]);
    sub = typed(catalog, &fields[4], PHYSICALITY_DESCRIPTOR_HILBERT, 17u);
    if (sub != NULL) for (size_t byte = 0; byte < 16u; ++byte)
        if (!byte_value(catalog->basis, &sub[byte + 1u], &input->hilbert_index.bytes[byte])) catalog->invalid = 1;
    if (trajectory_width(catalog, root, &carriers, &width)) {
        for (size_t i = 0; i < width; ++i) {
            const physicality_descriptor_node_t* carrier = require_node(catalog, &carriers[i]);
            if (carrier == NULL) continue;
            const hash128_t* carrier_fields = catalog->children + carrier->first_child;
            if (hash128_equals(carrier_fields, &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_CARRIER])) {
                sub = typed(catalog, &carriers[i], PHYSICALITY_DESCRIPTOR_CARRIER, 5u);
                if (sub == NULL) continue;
                mantissa_payload_t payload = {0};
                payload.entity_id = sub[1];
                if (word(catalog, &sub[2], PHYSICALITY_DESCRIPTOR_U16, 2u, &value)) payload.ordinal = (uint16_t)value;
                if (word(catalog, &sub[3], PHYSICALITY_DESCRIPTOR_U16, 2u, &value)) payload.run_length = (uint16_t)value;
                if (word(catalog, &sub[4], PHYSICALITY_DESCRIPTOR_U64, 8u, &payload.flags) &&
                    payload.flags > UINT64_C(0xfffffffffffff)) catalog->invalid = 1;
                if (trajectory != NULL) mantissa_pack(trajectory + i * 4u, &payload);
            } else {
                sub = typed(catalog, &carriers[i], PHYSICALITY_DESCRIPTOR_FACTOR, 5u);
                if (sub == NULL) continue;
                for (size_t axis = 0; axis < 4u; ++axis) {
                    double scratch = 0;
                    (void)binary64(catalog, &sub[axis + 1u], trajectory == NULL ? &scratch : &trajectory[i * 4u + axis]);
                }
            }
        }
    }
    input->trajectory_vertices = width;
    input->trajectory_xyzm = width == 0u ? NULL : trajectory;
    if (word(catalog, &fields[6], PHYSICALITY_DESCRIPTOR_I32, 4u, &value)) {
        if (value > INT32_MAX) catalog->invalid = 1;
        else input->n_constituents = (int32_t)value;
    }
    input->alignment_residual_is_null = hash128_equals(&fields[7], &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT]);
    if (!input->alignment_residual_is_null) {
        sub = typed(catalog, &fields[7], PHYSICALITY_DESCRIPTOR_PRESENT, 2u);
        if (sub != NULL) (void)binary64(catalog, &sub[1], &input->alignment_residual);
    }
    input->source_dim_is_null = hash128_equals(&fields[8], &catalog->basis->tags[PHYSICALITY_DESCRIPTOR_ABSENT]);
    if (!input->source_dim_is_null) {
        sub = typed(catalog, &fields[8], PHYSICALITY_DESCRIPTOR_PRESENT, 2u);
        if (sub != NULL && word(catalog, &sub[1], PHYSICALITY_DESCRIPTOR_I32, 4u, &value)) {
            if (value > INT32_MAX) catalog->invalid = 1;
            else input->source_dim = (int32_t)value;
        }
    }
    return !catalog->invalid && catalog->missing_count == 0u;
}

void physicality_descriptor_readback_free(physicality_descriptor_readback_t* readback) {
    if (readback == NULL) return;
    free(readback->inputs);
    free(readback->trajectories);
    free(readback->missing);
    free(readback);
}

static int array_bytes(size_t* bytes, size_t count, size_t width) {
    if (count > (SIZE_MAX - *bytes) / width) return 0;
    *bytes += count * width;
    return 1;
}

static int catalog_index(catalog_t* catalog, size_t node_count, size_t child_count) {
    const physicality_descriptor_node_t* nodes = catalog->nodes;
    const hash128_t* children = catalog->children;
    for (size_t i = 0; i < node_count; ++i) {
        hash128_t actual;
        size_t slot;
        const physicality_descriptor_node_t* node = &nodes[i];
        if (node->child_count < 2u || node->first_child > child_count ||
            node->child_count > child_count - node->first_child) return 0;
        hash128_merkle(0u, children + node->first_child, node->child_count, &actual);
        if (!hash128_equals(&actual, &node->id)) return 0;
        slot = slot_for(&node->id, catalog->mask);
        while (catalog->slots[slot] != 0u) {
            const physicality_descriptor_node_t* old = &nodes[catalog->slots[slot] - 1u];
            if (hash128_equals(&old->id, &node->id)) {
                if (old->child_count != node->child_count || memcmp(children + old->first_child,
                    children + node->first_child, node->child_count * sizeof(hash128_t)) != 0) return 0;
                break;
            }
            slot = (slot + 1u) & catalog->mask;
        }
        catalog->slots[slot] = i + 1u;
    }
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
    catalog_t catalog = {0};
    catalog.nodes = nodes; catalog.children = children; catalog.basis = basis;
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
    if (!catalog_index(&catalog, node_count, child_count)) goto done;
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
        readback->peak_bytes = bytes + physicality_descriptor_plan_peak_bytes(verified_plan);
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

int physicality_descriptor_readback_root_entity(
    const physicality_descriptor_node_t* node, const hash128_t* children,
    size_t child_count, const physicality_descriptor_basis_t* basis, hash128_t* out_entity) {
    hash128_t actual;
    if (node == NULL || children == NULL || basis == NULL || out_entity == NULL ||
        node->child_count != 9u || node->first_child > child_count ||
        node->child_count > child_count - node->first_child ||
        !hash128_equals(children + node->first_child, &basis->tags[PHYSICALITY_DESCRIPTOR_SCHEMA])) return 0;
    hash128_merkle(0u, children + node->first_child, node->child_count, &actual);
    if (!hash128_equals(&actual, &node->id)) return 0;
    *out_entity = children[node->first_child + 1u];
    return 1;
}

static int identity_compare(const void* a, const void* b) {
    return memcmp(a, b, sizeof(hash128_t));
}

physicality_descriptor_status_t physicality_descriptor_readback_prepare(
    const physicality_descriptor_node_t* nodes, size_t node_count,
    const hash128_t* children, size_t child_count, const hash128_t* roots, size_t root_count,
    const physicality_descriptor_basis_t* basis, const physicality_descriptor_limits_t* limits,
    size_t maximum_bytes, size_t maximum_content_hash_operands,
    physicality_descriptor_readback_t** out) {
    size_t slots = 1u, missing_slots = 1u, possible_missing, bytes = sizeof(physicality_descriptor_readback_t);
    catalog_t catalog = {0};
    physicality_descriptor_readback_t* result = NULL;
    physicality_descriptor_status_t status = PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    size_t content_hash_operands = 0u;
    if (out == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out = NULL;
    if ((node_count && nodes == NULL) || (child_count && children == NULL) || (root_count && roots == NULL) ||
        limits == NULL || !physicality_descriptor_basis_is_valid(basis)) return PHYSICALITY_DESCRIPTOR_INVALID;
    if (node_count > SIZE_MAX / 2u || child_count > SIZE_MAX - root_count)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    possible_missing = child_count + root_count;
    if (possible_missing > SIZE_MAX / 2u) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    while (slots < node_count * 2u) { if (slots > SIZE_MAX / 2u) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED; slots *= 2u; }
    while (missing_slots < possible_missing * 2u) { if (missing_slots > SIZE_MAX / 2u) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED; missing_slots *= 2u; }
    if (!array_bytes(&bytes, slots, sizeof(size_t)) || !array_bytes(&bytes, missing_slots, sizeof(size_t)) ||
        !array_bytes(&bytes, possible_missing, sizeof(hash128_t)) || bytes > maximum_bytes)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    catalog.nodes = nodes; catalog.children = children; catalog.basis = basis;
    catalog.mask = slots - 1u; catalog.missing_mask = missing_slots - 1u;
    catalog.slots = calloc(slots, sizeof(size_t));
    catalog.missing_slots = calloc(missing_slots, sizeof(size_t));
    catalog.missing = possible_missing == 0u ? NULL : calloc(possible_missing, sizeof(hash128_t));
    if (catalog.slots == NULL || catalog.missing_slots == NULL || (possible_missing && catalog.missing == NULL)) {
        status = PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED; goto done;
    }
    if (!catalog_index(&catalog, node_count, child_count)) goto done;
    for (size_t i = 0; i < root_count; ++i) {
        physicality_descriptor_input_t scratch = {0};
        (void)decode_one(&catalog, &roots[i], &scratch, NULL);
        if ((scratch.type == 1 || scratch.type == PHYSICALITY_DESCRIPTOR_RETENTION_TYPE) && scratch.n_constituents > 0) {
            if ((size_t)scratch.n_constituents > maximum_content_hash_operands - content_hash_operands) {
                status = PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED; goto done;
            }
            content_hash_operands += (size_t)scratch.n_constituents;
        }
    }
    if (catalog.invalid) goto done;
    if (catalog.missing_count != 0u) {
        result = calloc(1u, sizeof(*result));
        if (result == NULL) { status = PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED; goto done; }
        qsort(catalog.missing, catalog.missing_count, sizeof(hash128_t), identity_compare);
        result->missing = catalog.missing; catalog.missing = NULL;
        result->missing_count = catalog.missing_count;
        result->bytes = sizeof(*result) + possible_missing * sizeof(hash128_t);
        result->peak_bytes = bytes;
        *out = result; result = NULL;
        status = PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER;
    } else status = PHYSICALITY_DESCRIPTOR_OK;
 done:
    free(catalog.slots); free(catalog.missing_slots); free(catalog.missing);
    physicality_descriptor_readback_free(result);
    if (status == PHYSICALITY_DESCRIPTOR_OK) {
        status = physicality_descriptor_readback_build(nodes, node_count, children, child_count, roots,
            root_count, basis, limits, maximum_bytes, out);
        if (status == PHYSICALITY_DESCRIPTOR_OK) {
            if ((*out)->peak_bytes < bytes) (*out)->peak_bytes = bytes;
            (*out)->content_hash_operands = content_hash_operands;
        }
    }
    return status;
}

size_t physicality_descriptor_readback_content_hash_operands(const physicality_descriptor_readback_t* readback) {
    return readback == NULL ? 0u : readback->content_hash_operands;
}

const hash128_t* physicality_descriptor_readback_missing(
    const physicality_descriptor_readback_t* readback, size_t* count) {
    if (count != NULL) *count = readback == NULL ? 0u : readback->missing_count;
    return readback == NULL ? NULL : readback->missing;
}
