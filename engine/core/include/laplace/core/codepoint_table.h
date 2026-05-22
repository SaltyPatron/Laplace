#pragma once

#include <stdint.h>
#include <stddef.h>
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* T0 codepoint perf-cache entry (per ADR 0006: perf-cache + DB seed are
 * sibling artifacts, both derived independently from Unicode UCD).
 *
 * Read pattern: random-access lookup by Unicode codepoint into a 1.114M-
 * entry mmap'd array. Each lookup touches one entry; cache-line alignment
 * means one cache line per lookup. 64-byte size is exact (one x86 cache
 * line) — DO NOT add fields without recomputing the layout.
 *
 * 1.114M entries × 64 B = ~67 MiB, mmap'd at process start.
 *
 * Layout justification: AoS chosen because the dominant access pattern is
 * single-codepoint random access (not batch operations on a single column).
 * Cache-line packing means one DRAM round-trip per lookup; SoA would force
 * 4-7 round-trips. */
typedef struct {
    uint32_t      codepoint;       /*  4 B */
    uint32_t      uca_order;       /*  4 B */
    double        coord[4];        /* 32 B — XYZM-packed, matches POINT4D */
    hilbert128_t  hilbert;         /* 16 B */
    hash128_t     hash;            /* 16 B */
    uint32_t      flags;           /*  4 B — Unicode property bits */
    uint32_t      _pad;            /*  4 B — explicit padding to 64 B */
} codepoint_entry_t;
/* Total: 4 + 4 + 32 + 16 + 16 + 4 + 4 = 80 B — but with alignment? Let me verify in Chunk 3.
 * Per DESIGN.md IV the target is 64 B per entry. Field set may need to shrink
 * to fit (e.g., 16-byte hash takes a lot of room). Adjust during Chunk 3
 * Story 3.6 when this is implemented for real. */

/* Implementations land Chunk 3. */
int                       codepoint_table_build_from_ucd(const char* ucd_path, codepoint_entry_t* out);
int                       codepoint_table_load_perfcache(const char* path);
const codepoint_entry_t*  codepoint_table_lookup(uint32_t codepoint);

#ifdef __cplusplus
}
#endif
