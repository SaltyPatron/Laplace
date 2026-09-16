#include "physicality_descriptor_provider.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/modality_decimal.h"

/* Frozen vocabulary literals are at most 64 ASCII codepoints. The ordinary
 * text owner can form at most 4*n+1 nodes (512 allocated slots), with 89 bytes
 * per slot, less than 2 KiB of segmentation arrays and at most 4 KiB of emit
 * scratch. Even one growing coordinate-array allocation coexisting with its
 * old buffer fits this 64 KiB reservation. No arbitrary caller text enters
 * this initializer. This is an admitted upper bound, not measured process RSS. */
#define VOCABULARY_CONTENT_SCRATCH_RESERVATION (64u * 1024u)

const char* physicality_descriptor_generated_source_name(void) {
    return "substrate/source/PhysicalityDescriptorAdmission/v1";
}

static physicality_descriptor_status_t frozen_source_create(
    const char* name, size_t maximum_bytes, hash128_t* out_source_id, intent_stage_t** out_stage,
    size_t* out_peak_bytes) {
    tier_tree_t* tree = NULL;
    intent_stage_t* stage = NULL;
    tier_node_view_t source;
    hash128_t emitted;
    if (out_stage == NULL || out_source_id == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_stage = NULL;
    if (out_peak_bytes != NULL) *out_peak_bytes = 0u;
    if (!codepoint_table_is_loaded()) return PHYSICALITY_DESCRIPTOR_MISSING_FLOOR;
    if (strlen(name) > 64u) return PHYSICALITY_DESCRIPTOR_INVALID;
    if (maximum_bytes < VOCABULARY_CONTENT_SCRATCH_RESERVATION)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    stage = intent_stage_new_bounded(0u, maximum_bytes - VOCABULARY_CONTENT_SCRATCH_RESERVATION);
    if (stage == NULL) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    if (content_witness_tree_build((const uint8_t*)name, strlen(name), &tree) != 0 || tree == NULL ||
        content_witness_tree_root_node(tree, &source) != 0 ||
        content_witness_emit_tree(stage, tree, &source.id, NULL, 0u, &emitted) != 0 ||
        !hash128_equals(&source.id, &emitted) || intent_stage_allocation_failed(stage)) {
        const physicality_descriptor_status_t status = intent_stage_allocation_failed(stage) ?
            PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED : PHYSICALITY_DESCRIPTOR_INVALID_BODY;
        tier_tree_free(tree); intent_stage_free(stage);
        return status;
    }
    tier_tree_free(tree);
    *out_source_id = source.id;
    *out_stage = stage;
    if (out_peak_bytes != NULL)
        *out_peak_bytes = intent_stage_memory_peak_bytes(stage) + VOCABULARY_CONTENT_SCRATCH_RESERVATION;
    return PHYSICALITY_DESCRIPTOR_OK;
}

physicality_descriptor_status_t physicality_descriptor_generated_source_create(
    size_t maximum_bytes, hash128_t* out_source_id, intent_stage_t** out_stage,
    size_t* out_peak_bytes) {
    return frozen_source_create(physicality_descriptor_generated_source_name(),
        maximum_bytes, out_source_id, out_stage, out_peak_bytes);
}

const char* physicality_descriptor_session_source_name(void) {
    return "substrate/source/SessionProjection/v1";
}

physicality_descriptor_status_t physicality_descriptor_session_source_create(
    size_t maximum_bytes, hash128_t* out_source_id, intent_stage_t** out_stage,
    size_t* out_peak_bytes) {
    return frozen_source_create(physicality_descriptor_session_source_name(),
        maximum_bytes, out_source_id, out_stage, out_peak_bytes);
}

static const char* const vocabulary_tags[PHYSICALITY_DESCRIPTOR_TAG_COUNT] = {
    "PhysicalityDescriptorV1", "PhysicalityCoordinateV1", "PhysicalityHilbertV1",
    "PhysicalityTrajectoryV1", "PhysicalityCarrierV1", "PhysicalityFactorV1",
    "PhysicalityAbsentV1", "PhysicalityPresentV1", "PhysicalityI16LEV1",
    "PhysicalityI32LEV1", "PhysicalityU16LEV1", "PhysicalityU64LEV1",
    "PhysicalityBinary64LEV1"
};

static physicality_descriptor_status_t emit_vocabulary_content(
    physicality_descriptor_vocabulary_t* vocabulary, const hash128_t* source,
    const char* text, size_t bytes, tier_node_view_t* out_node) {
    tier_tree_t* tree = NULL;
    hash128_t emitted;
    int status;
    if (bytes == 0u || bytes > 64u) return PHYSICALITY_DESCRIPTOR_INVALID;
    for (size_t i = 0u; i < bytes; ++i)
        if ((unsigned char)text[i] >= 128u) return PHYSICALITY_DESCRIPTOR_INVALID;
    if (content_witness_tree_build((const uint8_t*)text, bytes, &tree) != 0 || tree == NULL)
        return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    status = content_witness_tree_root_node(tree, out_node);
    if (status == 0)
        status = content_witness_emit_tree(vocabulary->stage, tree, source, NULL, 0u, &emitted);
    tier_tree_free(tree);
    if (intent_stage_allocation_failed(vocabulary->stage))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    if (status != 0 || !hash128_equals(&emitted, &out_node->id))
        return PHYSICALITY_DESCRIPTOR_INVALID_BODY;
    const size_t resident = sizeof(*vocabulary) + intent_stage_memory_peak_bytes(vocabulary->stage) +
        VOCABULARY_CONTENT_SCRATCH_RESERVATION;
    if (resident > vocabulary->peak_bytes) vocabulary->peak_bytes = resident;
    return PHYSICALITY_DESCRIPTOR_OK;
}

physicality_descriptor_status_t physicality_descriptor_vocabulary_create(
    const hash128_t* source_id, size_t maximum_bytes,
    physicality_descriptor_vocabulary_t** out_vocabulary) {
    physicality_descriptor_vocabulary_t* vocabulary;
    physicality_descriptor_status_t status;
    if (out_vocabulary == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_vocabulary = NULL;
    if (source_id == NULL) return PHYSICALITY_DESCRIPTOR_INVALID;
    if (!codepoint_table_is_loaded())
        return (physicality_descriptor_status_t)PHYSICALITY_DESCRIPTOR_MISSING_FLOOR;
    if (maximum_bytes < sizeof(*vocabulary) + VOCABULARY_CONTENT_SCRATCH_RESERVATION)
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    vocabulary = calloc(1u, sizeof(*vocabulary));
    if (vocabulary == NULL) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    const int prepared = codepoint_table_prepare_id_index(
        maximum_bytes - sizeof(*vocabulary) - VOCABULARY_CONTENT_SCRATCH_RESERVATION,
        &vocabulary->floor_index_added_bytes);
    if (prepared != 0) { free(vocabulary); return prepared == -1 ?
        PHYSICALITY_DESCRIPTOR_MISSING_FLOOR : PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED; }
    vocabulary->stage = intent_stage_new_bounded(0u,
        maximum_bytes - sizeof(*vocabulary) - vocabulary->floor_index_added_bytes -
        VOCABULARY_CONTENT_SCRATCH_RESERVATION);
    if (vocabulary->stage == NULL) {
        free(vocabulary);
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    }
    if (codepoint_table_copy_receipt(&vocabulary->floor_receipt) != 0) {
        physicality_descriptor_vocabulary_free(vocabulary);
        return (physicality_descriptor_status_t)PHYSICALITY_DESCRIPTOR_MISSING_FLOOR;
    }
    for (size_t i = 0; i < PHYSICALITY_DESCRIPTOR_TAG_COUNT; ++i) {
        status = emit_vocabulary_content(vocabulary, source_id,
            vocabulary_tags[i], strlen(vocabulary_tags[i]), &vocabulary->tags[i]);
        if (status != PHYSICALITY_DESCRIPTOR_OK) goto failed;
        vocabulary->basis.tags[i] = vocabulary->tags[i].id;
    }
    for (uint32_t i = 0; i < 256u; ++i) {
        uint32_t codepoints[LAPLACE_DECIMAL_MAX_CPS];
        char digits[LAPLACE_DECIMAL_MAX_CPS];
        const uint32_t length = laplace_decimal_codepoints_u32(i, codepoints);
        for (size_t j = 0; j < length; ++j) digits[j] = (char)codepoints[j];
        status = emit_vocabulary_content(vocabulary, source_id, digits, length, &vocabulary->numbers[i]);
        if (status != PHYSICALITY_DESCRIPTOR_OK) goto failed;
        vocabulary->basis.byte_numbers[i] = vocabulary->numbers[i].id;
    }
    {
        const char* names[] = {"PhysicalityViewV1", "PhysicalityCurrentFloorAdmittedWinnerRecipeV1",
            "PhysicalityFloorReceiptV1", "PhysicalityReferenceSelectionV1", "PhysicalitySelectionScopeV1",
            "PhysicalitySourceUnitContextV1", "PhysicalitySourceIdentifierV1", "PhysicalitySourceUnitReceiptV1"};
        tier_node_view_t* nodes[] = {&vocabulary->view_schema, &vocabulary->view_recipe,
            &vocabulary->floor_schema, &vocabulary->selection_schema, &vocabulary->scope_schema,
            &vocabulary->context_schema, &vocabulary->source_schema, &vocabulary->unit_schema};
        for (size_t i = 0; i < 8u; ++i) {
            status = emit_vocabulary_content(vocabulary, source_id, names[i], strlen(names[i]), nodes[i]);
            if (status != PHYSICALITY_DESCRIPTOR_OK) goto failed;
        }
    }
    if (!physicality_descriptor_basis_is_valid(&vocabulary->basis)) {
        status = PHYSICALITY_DESCRIPTOR_INVALID;
        goto failed;
    }
    *out_vocabulary = vocabulary;
    return PHYSICALITY_DESCRIPTOR_OK;
failed:
    physicality_descriptor_vocabulary_free(vocabulary);
    return status;
}

void physicality_descriptor_vocabulary_free(physicality_descriptor_vocabulary_t* vocabulary) {
    if (vocabulary == NULL) return;
    intent_stage_free(vocabulary->stage);
    free(vocabulary);
}

const physicality_descriptor_basis_t* physicality_descriptor_vocabulary_basis(
    const physicality_descriptor_vocabulary_t* vocabulary) {
    return vocabulary == NULL ? NULL : &vocabulary->basis;
}

const hash128_t* physicality_descriptor_vocabulary_floor_receipt(
    const physicality_descriptor_vocabulary_t* vocabulary) {
    return vocabulary == NULL ? NULL : &vocabulary->floor_receipt;
}

intent_stage_t* physicality_descriptor_vocabulary_take_stage(physicality_descriptor_vocabulary_t* vocabulary) {
    intent_stage_t* stage;
    if (vocabulary == NULL) return NULL;
    stage = vocabulary->stage;
    vocabulary->stage = NULL;
    return stage;
}

size_t physicality_descriptor_vocabulary_bytes(const physicality_descriptor_vocabulary_t* vocabulary) {
    return vocabulary == NULL ? 0u : sizeof(*vocabulary) + intent_stage_memory_bytes(vocabulary->stage);
}

size_t physicality_descriptor_vocabulary_peak_bytes(const physicality_descriptor_vocabulary_t* vocabulary) {
    return vocabulary == NULL ? 0u : vocabulary->peak_bytes + vocabulary->floor_index_added_bytes;
}

size_t physicality_descriptor_vocabulary_floor_index_added_bytes(const physicality_descriptor_vocabulary_t* vocabulary) {
    return vocabulary == NULL ? 0u : vocabulary->floor_index_added_bytes;
}
