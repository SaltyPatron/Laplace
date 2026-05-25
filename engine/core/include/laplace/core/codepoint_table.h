#pragma once

#include <stdint.h>
#include <stddef.h>
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/perfcache_format.h"

#ifdef __cplusplus
extern "C" {
#endif

/* T0 codepoint perf-cache runtime accessor (per ADR 0006 + ADR 0053).
 *
 * The perf-cache is APP/reference data: a derived hot-path projection of
 * the substrate's canonical Unicode T0 layer, produced at build time by
 * laplace_ucd_tables_emit from UCDXML + DUCET. This module mmaps it and
 * exposes O(1) per-codepoint lookup so the runtime (TextDecomposer +
 * HashComposer + the UAX#29/NFC state machines) resolves codepoint
 * id / coord / hilbert / segmentation+NFC properties without touching
 * the database — the substrate's T>0 composition is computable
 * in-process. The 1.1M-entity + physicality DB seed is the ADR-0006
 * sibling, derived independently; no complex attestations for T0.
 *
 * The record IS laplace_perfcache_record_t (perfcache_format.h), 80 B. */
typedef laplace_perfcache_record_t codepoint_entry_t;

/* Load + validate the perf-cache from a file path and install it as the
 * process-wide table. Returns 0 on success; non-zero on:
 *   -1 open/stat/mmap failure
 *   -2 bad magic / unsupported version
 *   -3 record_count / record_size mismatch
 *   -4 body-CRC mismatch (corruption)
 * A second successful load replaces the first (prior mapping unmapped).
 * NULL path is reserved for the embedded-.rodata variant (ADR 0054) —
 * not yet wired; returns -1. */
int codepoint_table_load_perfcache(const char* path);

/* Unmap + clear the process-wide table. Safe when not loaded. */
void codepoint_table_unload(void);

/* True iff a perf-cache is currently loaded. */
int codepoint_table_is_loaded(void);

/* O(1) direct-index lookup. NULL if no table loaded or cp out of range.
 * Returned pointer valid until unload/reload. */
const codepoint_entry_t* codepoint_table_lookup(uint32_t codepoint);

/* Property accessors (unpack flags). Return the value id, or DEFAULT (0)
 * when no table loaded / cp out of range. Ids per ucd_property_values.h. */
uint8_t codepoint_table_gb(uint32_t codepoint);
uint8_t codepoint_table_wb(uint32_t codepoint);
uint8_t codepoint_table_sb(uint32_t codepoint);
uint8_t codepoint_table_incb(uint32_t codepoint);
uint8_t codepoint_table_ccc(uint32_t codepoint);

/* NFC canonical decomposition of `cp`. On hit, writes a pointer to the
 * decomposition codepoint array + its length, returns 1; else 0. Pointer
 * references the mmap'd blob (valid until unload). */
int codepoint_table_decompose(uint32_t cp, const uint32_t** out_seq, uint32_t* out_len);

/* NFC canonical composition of (first, second). On hit, writes composed
 * codepoint, returns 1; else 0. */
int codepoint_table_compose(uint32_t first, uint32_t second, uint32_t* out_composed);

#ifdef __cplusplus
}
#endif
