#ifndef LAPLACE_PHYSICALITY_DESCRIPTOR_ADMISSION_PG_H
#define LAPLACE_PHYSICALITY_DESCRIPTOR_ADMISSION_PG_H

#include "postgres.h"
#include "utils/memutils.h"
#include "laplace/core/physicality_descriptor_admission.h"

/* Three generated stages, borrowed until release: source declaration,
 * vocabulary, then descriptors/views/structural observations. They are never
 * automatically reflected again as new source observations. */
typedef struct laplace_physicality_pg_admission_result {
    MemoryContext owner;
    intent_stage_t *stages[3];
    const physicality_descriptor_admitted_form_t *forms;
    size_t form_count;
    hash128_t floor_receipt;
    hash128_t generated_source_id;
    Datum snapshot_receipt;
    size_t current_bodies, missing_bodies, floor_index_added_bytes;
    size_t retained_bytes, peak_bytes, logical_work, stored_vertices;
    int provider_rounds, database_operations;
} laplace_physicality_pg_admission_result;

/* The caller supplies actual bodies and explicit source/unit/prior metadata.
 * Input stages are borrowed; validated copies and every generated native
 * allocation live in the returned owner's child memory context. Provider reads
 * use the caller's active snapshot, which must be acquired after any write-lock
 * wait. Backend errors reclaim native allocations through context callbacks. */
laplace_physicality_pg_admission_result *laplace_physicality_pg_materialize(
    const intent_stage_t *const *source_stages, size_t source_stage_count,
    const intent_stage_t *const *admitted_stages, size_t admitted_stage_count,
    const physicality_descriptor_source_observation_t *observations, size_t observation_count,
    int64 generated_at_unix_us, size_t maximum_bytes,
    int maximum_database_operations, size_t maximum_logical_occurrences);

void laplace_physicality_pg_admission_release(laplace_physicality_pg_admission_result *result);

#endif
