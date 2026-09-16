#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct intent_stage intent_stage_t;
/* Conservative complete E/P/A tuple payload, using the ordinary serializer's
 * actual field widths and raw trajectory vertices. Excludes COPY stream and
 * array wrappers, buffer capacity, allocator metadata and admission graphs.
 * 0 succeeds; -1 invalid shape/output; -2 size overflow. Output unchanged on
 * refusal. No rows are created and no body authenticity is asserted. */
int intent_stage_tuple_payload_bound(size_t entity_count, size_t physicality_count,
    size_t stored_vertices, size_t attestation_count, size_t* out_bytes);


typedef enum {
    INTENT_STAGE_TABLE_ENTITIES      = 1,
    INTENT_STAGE_TABLE_PHYSICALITIES = 2,
    INTENT_STAGE_TABLE_ATTESTATIONS  = 3,
} intent_stage_table_t;

#define INTENT_STAGE_PG_EPOCH_UNIX_US INT64_C(946684800000000)

intent_stage_t* intent_stage_new(size_t row_capacity_hint);
/* Same serializer with an admitted requested-payload ceiling. Every bounded
 * buffer growth reserves old plus requested new storage before realloc, even
 * when libc can grow in place. Allocator bookkeeping/process RSS are excluded. */
intent_stage_t* intent_stage_new_bounded(size_t row_capacity_hint, size_t maximum_bytes);
void            intent_stage_free(intent_stage_t* stage);
/* Exclusive-owner lifetime operation for a stage whose entity/attestation
 * rows are no longer needed. Frees those table capacities and the entity
 * witness cache, resets their counts, and returns the exact released payload.
 * Physicality rows, bytes and borrowed physicality pointers remain unchanged;
 * borrowed entity/attestation pointers become invalid. The stage's historical
 * peak is unchanged. NULL and repeated calls return zero. This does not
 * validate input: admission callers must finish full tuple import first. */
size_t intent_stage_retain_physicalities(intent_stage_t* stage);
size_t intent_stage_memory_bytes(const intent_stage_t* stage);
/* Conservative high-water reservation of requested payload. Bounded growth
 * includes old plus requested new buffers even if realloc grows in place;
 * this is an upper bound, not measured simultaneous allocations or process RSS.
 * Unbounded buffer growth retains its historical retained-capacity accounting;
 * witness rehash keeps its existing old-plus-new accounting. */
size_t intent_stage_memory_peak_bytes(const intent_stage_t* stage);
int intent_stage_allocation_failed(const intent_stage_t* stage);

/* Bulk transport of the native tuple buffers. Validates exact row framing and
 * table field counts; typed consumers retain semantic validation ownership.
 * Returns 0, -1 for malformed input, or -2 for the allocation limit. */
int intent_stage_from_tuple_bytes(
    const uint8_t* entities, size_t entity_bytes,
    const uint8_t* physicalities, size_t physicality_bytes,
    const uint8_t* attestations, size_t attestation_bytes,
    size_t maximum_bytes, intent_stage_t** out_stage);

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
    int64_t          sum_score_fp1e9,
    int64_t          opponent_rd_fp1e9,
    int64_t          opponent_rating_fp1e9,
    const uint8_t*   highway_mask);

/* Same COPY row with an explicit durable replay disposition.  The historical
 * entry point above remains the replayable-evidence default. */
int intent_stage_add_attestation_mode(
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
    int64_t          sum_score_fp1e9,
    int64_t          opponent_rd_fp1e9,
    int64_t          opponent_rating_fp1e9,
    uint8_t          fold_replayable,
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

/* Versioned semantic replay digest. Row order and stage partitioning do not
 * matter; duplicate multiplicity does. Observation timestamps are excluded. */
int intent_stage_semantic_digest_batch(const intent_stage_t* const* stages,
                                      size_t count, hash128_t* out);
int intent_stage_semantic_digest(const intent_stage_t* stage, hash128_t* out);

int intent_stage_witness_seen(const intent_stage_t* stage, const hash128_t* id);
int intent_stage_witness_record(intent_stage_t* stage, const hash128_t* id);

/* Same content can occur at multiple representation floors. If its entity row
 * is already staged, retain the lowest observed floor without minting a second
 * row for the same id. Returns 1 when an entity row was found, 0 when absent,
 * and -1 for invalid input. */
int intent_stage_lower_entity_tier(intent_stage_t* stage, const hash128_t* id, int16_t tier);

/*
 * Splits `src` into `part_count` new stages, each safe to commit as an independent
 * transaction: every row partitions by the id of the entity it is "about" (an
 * entity's own id; a physicality's entity_id; an attestation's subject_id), so an
 * entity and every physicality/attestation whose subject is that entity always land
 * in the same output partition. Physicality rows within each output partition are
 * additionally ordered by Hilbert index for storage locality -- that ordering never
 * affects partition assignment. Callers must NOT further split an output partition's
 * rows across independent transactions, or this guarantee is lost.
 */
int intent_stage_partition(
    const intent_stage_t* src,
    size_t                part_count,
    intent_stage_t**      out_parts);

#ifdef __cplusplus
}
#endif
