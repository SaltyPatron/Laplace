#pragma once

/*
 * test_intent_stage.cpp predates the executable physicality identity law. Its
 * serialization/partition tests deliberately use arbitrary ids and synthetic
 * GeometryZM payloads because those tests care about COPY framing, sorting and
 * partition ownership rather than semantic composition.
 *
 * Normalize only that legacy fixture translation unit before it calls the real
 * API. The production function remains fail-closed, and the dedicated
 * test_intent_stage_physicality_law.cpp calls it directly with no adapter.
 */
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/trajectory.h"

static inline int intent_stage_add_physicality_legacy_fixture(
    intent_stage_t*     stage,
    const hash128_t*    id,
    const hash128_t*    entity_id,
    int16_t             type,
    const double        coord[4],
    const hilbert128_t* hilbert_index,
    const double*       trajectory_xyzm,
    uint32_t            trajectory_n_vertices,
    int32_t             n_constituents,
    int                 alignment_residual_is_null,
    double              alignment_residual,
    int                 source_dim_is_null,
    int32_t             source_dim,
    int64_t             observed_at_unix_us) {
    hash128_t normalized_entity = *entity_id;

    if (type == 1 && trajectory_xyzm && trajectory_n_vertices > 0) {
        hash128_t manifest;
        size_t logical_count = 0;
        if (trajectory_content_identity(
                trajectory_xyzm, trajectory_n_vertices,
                &manifest, &logical_count) == 0
            && logical_count == (size_t)n_constituents) {
            normalized_entity = manifest;
        }
    }

    hash128_t normalized_id;
    laplace_physicality_id_compute(normalized_entity, type, &normalized_id);

    /* Existing assertions inspect these local fixture variables after the call. */
    *(hash128_t*)id = normalized_id;
    *(hash128_t*)entity_id = normalized_entity;

    return intent_stage_add_physicality(
        stage, &normalized_id, &normalized_entity, type, coord, hilbert_index,
        trajectory_xyzm, trajectory_n_vertices, n_constituents,
        alignment_residual_is_null, alignment_residual,
        source_dim_is_null, source_dim, observed_at_unix_us);
}

#define intent_stage_add_physicality intent_stage_add_physicality_legacy_fixture
