#pragma once

#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/byte_atoms.h"

/* Private copied provider state. Native callers hold the floor publication
 * gate while constructing/using this provider; no mmap pointer is retained. */
struct physicality_descriptor_vocabulary {
    physicality_descriptor_basis_t basis;
    tier_node_view_t tags[PHYSICALITY_DESCRIPTOR_TAG_COUNT];
    tier_node_view_t numbers[256];
    tier_node_view_t view_schema;
    tier_node_view_t view_recipe;
    tier_node_view_t floor_schema;
    tier_node_view_t byte_floor_schema;
    tier_node_view_t retention_reference_schema;
    tier_node_view_t selection_schema;
    tier_node_view_t scope_schema;
    tier_node_view_t context_schema;
    tier_node_view_t source_schema;
    tier_node_view_t unit_schema;
    hash128_t floor_receipt;
    laplace_byte_atoms_t byte_basis;
    intent_stage_t* stage;
    size_t floor_index_added_bytes;
    size_t peak_bytes;
};
