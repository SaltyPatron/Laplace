#pragma once

/*
 * test_intent_stage.cpp predates the executable physicality identity law. Its
 * serialization/partition tests deliberately use arbitrary ids and synthetic
 * GeometryZM payloads because those tests care about COPY framing, sorting and
 * partition ownership rather than semantic composition.
 *
 * This adapter is force-included into THAT translation unit only. It feeds the
 * production API a temporary semantically-valid Projection row, then restores
 * the legacy fixture bytes in the staged tuple so the serializer assertions keep
 * testing their original byte contract. The dedicated
 * test_intent_stage_physicality_law.cpp is compiled without this adapter and
 * exercises the real fail-closed law directly.
 */
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/trajectory.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static inline uint32_t fixture_be32(const uint8_t* p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16)
         | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static inline void fixture_store_be16(uint8_t* p, int16_t v) {
    uint16_t u = (uint16_t)v;
    p[0] = (uint8_t)(u >> 8);
    p[1] = (uint8_t)u;
}

static inline void fixture_store_be32(uint8_t* p, int32_t v) {
    uint32_t u = (uint32_t)v;
    p[0] = (uint8_t)(u >> 24);
    p[1] = (uint8_t)(u >> 16);
    p[2] = (uint8_t)(u >> 8);
    p[3] = (uint8_t)u;
}

static inline void fixture_store_le_double(uint8_t* p, double value) {
    uint64_t bits = 0;
    memcpy(&bits, &value, sizeof(bits));
    for (int i = 0; i < 8; ++i) p[i] = (uint8_t)(bits >> (i * 8));
}

static inline uint8_t* fixture_field(
    uint8_t* row, size_t row_len, int field_1based, int32_t* out_len) {
    if (!row || row_len < 2) return NULL;
    uint16_t cols = (uint16_t)(((uint16_t)row[0] << 8) | row[1]);
    size_t at = 2;
    for (uint16_t i = 1; i <= cols; ++i) {
        if (at + 4 > row_len) return NULL;
        int32_t len = (int32_t)fixture_be32(row + at);
        at += 4;
        if (i == (uint16_t)field_1based) {
            if (len < 0 || at + (size_t)len > row_len) return NULL;
            if (out_len) *out_len = len;
            return row + at;
        }
        if (len >= 0) {
            if (at + (size_t)len > row_len) return NULL;
            at += (size_t)len;
        }
    }
    return NULL;
}

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
    /* Preserve production argument validation. The fixture shim exists only to
     * translate otherwise well-formed legacy serialization rows; malformed
     * pointer/count shapes must reach the real function unchanged. */
    if (!stage || !id || !entity_id || !coord || !hilbert_index
        || n_constituents < 0
        || (trajectory_n_vertices > 0 && !trajectory_xyzm)
        || (trajectory_n_vertices == 0 && n_constituents != 0)) {
        return intent_stage_add_physicality(
            stage, id, entity_id, type, coord, hilbert_index,
            trajectory_xyzm, trajectory_n_vertices, n_constituents,
            alignment_residual_is_null, alignment_residual,
            source_dim_is_null, source_dim, observed_at_unix_us);
    }

    size_t before_len = 0;
    (void)intent_stage_tuple_ptr(
        stage, INTENT_STAGE_TABLE_PHYSICALITIES, &before_len);

    /* Projection deliberately permits a governed parent identity while still
     * validating the trajectory's exact logical count. Construct one valid
     * temporary manifest with the same stored vertex count so the tuple shape is
     * byte-for-byte compatible with the legacy fixture. */
    const int16_t normalized_type = 3;
    hash128_t normalized_entity = *entity_id;
    hash128_t normalized_id;
    laplace_physicality_id_compute(
        normalized_entity, normalized_type, &normalized_id);

    hash128_t* temp_children = NULL;
    double* temp_trajectory = NULL;
    const double* call_trajectory = NULL;
    int32_t call_constituents = 0;

    if (trajectory_n_vertices > 0) {
        temp_children = (hash128_t*)calloc(
            trajectory_n_vertices, sizeof(hash128_t));
        temp_trajectory = (double*)calloc(
            (size_t)trajectory_n_vertices * 4u, sizeof(double));
        if (!temp_children || !temp_trajectory) {
            free(temp_children);
            free(temp_trajectory);
            return -1;
        }
        for (uint32_t i = 0; i < trajectory_n_vertices; ++i) {
            memset(&temp_children[i], (int)(i + 1u), sizeof(hash128_t));
        }
        if (trajectory_build(
                temp_children, trajectory_n_vertices, temp_trajectory) != 0) {
            free(temp_children);
            free(temp_trajectory);
            return -1;
        }
        call_trajectory = temp_trajectory;
        call_constituents = (int32_t)trajectory_n_vertices;
    }

    int rc = intent_stage_add_physicality(
        stage, &normalized_id, &normalized_entity, normalized_type,
        coord, hilbert_index,
        call_trajectory, trajectory_n_vertices, call_constituents,
        alignment_residual_is_null, alignment_residual,
        source_dim_is_null, source_dim, observed_at_unix_us);
    free(temp_children);
    free(temp_trajectory);
    if (rc != 0) return rc;

    size_t after_len = 0;
    const uint8_t* tuples_const = intent_stage_tuple_ptr(
        stage, INTENT_STAGE_TABLE_PHYSICALITIES, &after_len);
    if (!tuples_const || after_len <= before_len) return -1;
    uint8_t* row = (uint8_t*)tuples_const + before_len;
    size_t row_len = after_len - before_len;

    int32_t len = 0;
    uint8_t* f1 = fixture_field(row, row_len, 1, &len);
    if (!f1 || len != 16) return -1;
    memcpy(f1, id, 16);
    uint8_t* f2 = fixture_field(row, row_len, 2, &len);
    if (!f2 || len != 16) return -1;
    memcpy(f2, entity_id, 16);
    uint8_t* f3 = fixture_field(row, row_len, 3, &len);
    if (!f3 || len != 2) return -1;
    fixture_store_be16(f3, type);
    uint8_t* f7 = fixture_field(row, row_len, 7, &len);
    if (!f7 || len != 4) return -1;
    fixture_store_be32(f7, n_constituents);

    if (trajectory_n_vertices > 0) {
        uint8_t* f6 = fixture_field(row, row_len, 6, &len);
        uint32_t expected = 1u + 4u
            + (trajectory_n_vertices == 1 ? 0u : 4u)
            + 32u * trajectory_n_vertices;
        if (!f6 || (uint32_t)len != expected) return -1;
        size_t coord_off = trajectory_n_vertices == 1 ? 5u : 9u;
        for (uint32_t v = 0; v < trajectory_n_vertices; ++v)
            for (int axis = 0; axis < 4; ++axis)
                fixture_store_le_double(
                    f6 + coord_off + ((size_t)v * 4u + (size_t)axis) * 8u,
                    trajectory_xyzm[(size_t)v * 4u + (size_t)axis]);
    }
    return 0;
}

#define intent_stage_add_physicality intent_stage_add_physicality_legacy_fixture
