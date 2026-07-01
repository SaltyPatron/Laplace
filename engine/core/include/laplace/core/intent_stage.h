#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct intent_stage intent_stage_t;

typedef enum {
    INTENT_STAGE_TABLE_ENTITIES      = 1,
    INTENT_STAGE_TABLE_PHYSICALITIES = 2,
    INTENT_STAGE_TABLE_ATTESTATIONS  = 3,
} intent_stage_table_t;

#define INTENT_STAGE_PG_EPOCH_UNIX_US INT64_C(946684800000000)

intent_stage_t* intent_stage_new(size_t row_capacity_hint);
void            intent_stage_free(intent_stage_t* stage);

size_t intent_stage_entity_count(const intent_stage_t* stage);
size_t intent_stage_physicality_count(const intent_stage_t* stage);
size_t intent_stage_attestation_count(const intent_stage_t* stage);

const char* intent_stage_copy_column_list(intent_stage_table_t table);

int intent_stage_add_entity(
    intent_stage_t*  stage,
    const hash128_t* id,
    int16_t          tier,
    const hash128_t* type_id,
    const hash128_t* first_observed_by);

int intent_stage_add_physicality(
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
    int64_t             observed_at_unix_us);

int intent_stage_add_attestation(
    intent_stage_t*  stage,
    const hash128_t* id,
    const hash128_t* subject_id,
    const hash128_t* type_id,
    const hash128_t* object_id,
    const hash128_t* source_id,
    const hash128_t* context_id,
    int16_t          outcome,
    int64_t          last_observed_at_unix_us,
    int64_t          observation_count,
    const uint8_t*   highway_mask);

size_t intent_stage_emit_copy_binary(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    uint8_t*              buf,
    size_t                buf_capacity);

const uint8_t* intent_stage_tuple_ptr(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    size_t*               out_len);

int intent_stage_witness_seen(const intent_stage_t* stage, const hash128_t* id);
int intent_stage_witness_record(intent_stage_t* stage, const hash128_t* id);

int intent_stage_partition(
    const intent_stage_t* src,
    size_t                part_count,
    intent_stage_t**      out_parts);

#ifdef __cplusplus
}
#endif
