#pragma once

#include <stddef.h>
#include "laplace/core/perfcache_format.h"
#include "laplace/core/content_witness_batch.h"

#ifdef __cplusplus
extern "C" {
#endif

int laplace_unicode_seed_compute(const char* ucdxml_path,
                                 const char* ducet_path,
                                 laplace_perfcache_record_t* out_records,
                                 size_t out_capacity);

int laplace_unicode_seed_validate_ucdxml(const char* ucdxml_path);

typedef struct laplace_unicode_seed_snapshot laplace_unicode_seed_snapshot_t;

/* Parse the authoritative UCDXML + DUCET inputs once into an immutable in-memory
 * seed snapshot. This is source-ingest working state, not the installed perfcache.
 * Unicode admission stages PostgreSQL rows from this snapshot; the runtime ROM is
 * downstream derived state and must never feed the database seed. */
int laplace_unicode_seed_snapshot_open(
    const char* ucdxml_path,
    const char* ducet_path,
    laplace_unicode_seed_snapshot_t** out_snapshot);
void laplace_unicode_seed_snapshot_free(laplace_unicode_seed_snapshot_t* snapshot);
size_t laplace_unicode_seed_snapshot_count(const laplace_unicode_seed_snapshot_t* snapshot);

/* Copy a bounded slice of the exact inflated UCDXML member retained by the
 * source snapshot. The generic XML provider consumes this stream without a
 * second physical-artifact open. */
int laplace_unicode_seed_snapshot_xml_copy(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t offset,
    uint8_t* destination,
    size_t destination_capacity,
    size_t* out_copied);

/* Stage one bounded contiguous codepoint range directly into the native ingest
 * IntentStage: one tier-0 Codepoint entity and one atomic Content physicality
 * per codepoint. One managed/native crossing owns the complete range. */
int laplace_unicode_seed_snapshot_stage(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t first,
    size_t count,
    intent_stage_t* stage,
    const hash128_t* source_id);

/* Exact source-derived record readback for parity/tests; never a runtime cache API. */
int laplace_unicode_seed_snapshot_copy_record(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t index,
    laplace_perfcache_record_t* out_record);

#ifdef __cplusplus
}
#endif
