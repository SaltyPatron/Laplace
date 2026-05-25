#pragma once

#include <stddef.h>
#include "laplace/core/perfcache_format.h"

#ifdef __cplusplus
extern "C" {
#endif

/* T0 codepoint seed — the ONE place the substrate's per-codepoint records are
 * computed. Parses UCDXML (UAX#42) + DUCET (UCA allkeys.txt), assigns DUCET
 * collation rank, places each codepoint via super_fibonacci on S^3, BLAKE3-128
 * of its UTF-8 bytes for the entity id, Skilling Hilbert-encodes the coord,
 * packs the segmentation/NFC flags. Writes 1,114,112 records into out_records.
 *
 * Two consumers feed off this single source of truth:
 *   - perf-cache emitter: calls this, then appends decomp/compose side-tables
 *     + header/trailer and writes the blob to disk
 *   - UnicodeDecomposer (C#): calls this via P/Invoke, marshals the buffer
 *     into laplace.entities + laplace.physicalities
 * Byte-identical buffer ⇒ blob and DB seed cannot diverge.
 *
 * out_records must point at storage for at least LAPLACE_PERFCACHE_RECORD_COUNT
 * records (1,114,112 × 80 B = ~85 MiB). Returns 0 on success, negative on:
 *   -1 null arg / out_capacity too small
 *   -2 UCDXML open / parse failure
 *   -3 DUCET open / parse failure */
int laplace_unicode_seed_compute(const char* ucdxml_path,
                                 const char* ducet_path,
                                 laplace_perfcache_record_t* out_records,
                                 size_t out_capacity);

#ifdef __cplusplus
}
#endif
