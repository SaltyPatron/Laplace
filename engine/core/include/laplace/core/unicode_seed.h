#pragma once

#include <stddef.h>
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"

#ifdef __cplusplus
extern "C" {
#endif

int laplace_unicode_seed_validate_ucdxml(const char* ucdxml_path);

typedef struct laplace_unicode_seed_snapshot laplace_unicode_seed_snapshot_t;

/* Retain the source XML bytes for semantic ingestion. Scalar identities and
 * coordinates refer to the one Native floor; no second table is generated. */
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

/* Native 64-byte scalar record readback; never admits floor rows to PostgreSQL. */
int laplace_unicode_seed_snapshot_copy_record(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t index,
    codepoint_entry_t* out_record);

#ifdef __cplusplus
}
#endif
