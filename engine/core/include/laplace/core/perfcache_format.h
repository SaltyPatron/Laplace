#pragma once

#include <stdint.h>
#include <stddef.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* perfcache_format — on-disk layout of the T0 codepoint perf-cache
 * (sibling artifact, build pipeline). APP DATA: a
 * derived hot-path index over the substrate's canonical Unicode graph,
 * NOT the authoritative record. The authoritative Unicode record is the
 * referential entity/attestation graph UnicodeDecomposer seeds into PG
 * (substrate data); this binary is the flattened projection the runtime
 * (TextDecomposer + HashComposer) mmaps so it can compute T>0 without
 * touching the database.
 *
 * Shared contract: laplace_perfcache_emit writes this; the runtime
 * codepoint_table loader reads it. Same struct definitions on both
 * sides, byte-for-byte.
 *
 * Determinism: same UCD + UCA version + same emit-tool source
 * → byte-identical perfcache on every machine.
 *
 * This extends locked format (format_version 1, header-only
 * + fixed records) to format_version 2 — adds a section directory in the
 * header for the NFC decomposition + composition side-tables, which the
 * runtime NFC path needs and which don't fit a fixed-width record. The
 * record itself grows to 80 bytes (the 64 in was aspirational;
 * coord[4]f64 + hilbert128 + hash128 = 64 alone, before codepoint /
 * uca_order / flags / pad). amended to match. */

#define LAPLACE_PERFCACHE_MAGIC 0x4652504Cu /* 'L','P','R','F' little-endian (stated hex was wrong) */
#define LAPLACE_PERFCACHE_VERSION 2u
#define LAPLACE_PERFCACHE_RECORD_COUNT 1114112u /* full Unicode codespace 0..0x10FFFF */

/* Per-codepoint fixed record. 80 bytes. Memory layout matches
 * codepoint_table.h's codepoint_entry_t exactly; this header is the
 * canonical definition both the emitter and loader share.
 *
 * The `flags` field packs the scalar segmentation/normalization
 * properties the runtime state machines consult (see LAPLACE_PERFCACHE_*
 * bitfield macros below). The richer relational properties (script,
 * block, general category, case mappings, decomposition-as-entity-edges)
 * are NOT here — those live in the substrate graph; this index carries
 * only what the runtime hot path computes T>0 from. */
typedef struct {
    uint32_t     codepoint;   /*  4 B */
    uint32_t     uca_order;   /*  4 B — DUCET collation rank; index into super-Fibonacci */
    double       coord[4];    /* 32 B — XYZM, super-Fibonacci(uca_order) on S^3 */
    hilbert128_t hilbert;     /* 16 B — Skilling encode of coord */
    hash128_t    hash;        /* 16 B — BLAKE3-128 of the codepoint's UTF-8 bytes */
    uint32_t     flags;       /*  4 B — packed GB/WB/SB/InCB/CCC + spare */
    uint32_t     _pad;        /*  4 B — explicit pad; reserved for future bits */
} laplace_perfcache_record_t;  /* total 80 B */

/* flags bitfield layout. Stable across format version 2.
 *   bits  0..3   GB  (Grapheme_Cluster_Break value id, 0..15)
 *   bits  4..8   WB  (Word_Break value id, 0..31)
 *   bits  9..12  SB  (Sentence_Break value id, 0..15)
 *   bits 13..14  InCB (Indic_Conjunct_Break value id, 0..3)
 *   bits 15..22  CCC (Canonical_Combining_Class, 0..255)
 *   bits 23..31  reserved (9 bits) */
#define LAPLACE_PC_GB_SHIFT   0u
#define LAPLACE_PC_GB_MASK    0x0000000Fu
#define LAPLACE_PC_WB_SHIFT   4u
#define LAPLACE_PC_WB_MASK    0x000001F0u
#define LAPLACE_PC_SB_SHIFT   9u
#define LAPLACE_PC_SB_MASK    0x00001E00u
#define LAPLACE_PC_INCB_SHIFT 13u
#define LAPLACE_PC_INCB_MASK  0x00006000u
#define LAPLACE_PC_CCC_SHIFT  15u
#define LAPLACE_PC_CCC_MASK   0x007F8000u

static inline uint32_t laplace_pc_pack_flags(uint8_t gb, uint8_t wb, uint8_t sb,
                                             uint8_t incb, uint8_t ccc) {
    return ((uint32_t)gb   << LAPLACE_PC_GB_SHIFT)
         | ((uint32_t)wb   << LAPLACE_PC_WB_SHIFT)
         | ((uint32_t)sb   << LAPLACE_PC_SB_SHIFT)
         | ((uint32_t)incb << LAPLACE_PC_INCB_SHIFT)
         | ((uint32_t)ccc  << LAPLACE_PC_CCC_SHIFT);
}
static inline uint8_t laplace_pc_gb(uint32_t f)   { return (uint8_t)((f & LAPLACE_PC_GB_MASK)   >> LAPLACE_PC_GB_SHIFT); }
static inline uint8_t laplace_pc_wb(uint32_t f)   { return (uint8_t)((f & LAPLACE_PC_WB_MASK)   >> LAPLACE_PC_WB_SHIFT); }
static inline uint8_t laplace_pc_sb(uint32_t f)   { return (uint8_t)((f & LAPLACE_PC_SB_MASK)   >> LAPLACE_PC_SB_SHIFT); }
static inline uint8_t laplace_pc_incb(uint32_t f) { return (uint8_t)((f & LAPLACE_PC_INCB_MASK) >> LAPLACE_PC_INCB_SHIFT); }
static inline uint8_t laplace_pc_ccc(uint32_t f)  { return (uint8_t)((f & LAPLACE_PC_CCC_MASK)  >> LAPLACE_PC_CCC_SHIFT); }

/* NFC decomposition side-table record: canonical decomposition of a
 * codepoint as a (start,len) slice into the flat decomp_data array.
 * Each entry in decomp_data is itself a codepoint (an entity reference
 * in the substrate graph; here flattened to its scalar value for the
 * runtime NFC path). Sorted by cp for binary search. */
typedef struct {
    uint32_t cp;
    uint32_t start_idx;   /* into decomp_data */
    uint32_t length;      /* count of codepoints */
} laplace_perfcache_decomp_t;

/* NFC composition side-table record: (first, second) -> composed.
 * Sorted by (first, second) for binary search. Composition exclusions
 * + non-starter-decomposition filtering already applied at emit time. */
typedef struct {
    uint32_t first;
    uint32_t second;
    uint32_t composed;
} laplace_perfcache_compose_t;

/* File header. 128 bytes (two cache lines). All multi-byte fields
 * little-endian (x86_64). Offsets are byte offsets from the start of
 * the file. */
typedef struct {
    uint32_t magic;                  /* LAPLACE_PERFCACHE_MAGIC */
    uint32_t format_version;         /* LAPLACE_PERFCACHE_VERSION */
    char     ucd_version[8];         /* e.g. "17.0.0\0\0" */
    char     uca_version[8];         /* e.g. "17.0.0\0\0" */
    uint64_t record_count;           /* = LAPLACE_PERFCACHE_RECORD_COUNT */
    uint64_t record_size;            /* = sizeof(laplace_perfcache_record_t) = 80 */
    uint64_t records_offset;         /* byte offset to records section */
    uint64_t decomp_record_count;
    uint64_t decomp_records_offset;
    uint64_t decomp_data_count;      /* number of uint32_t in flat decomp_data */
    uint64_t decomp_data_offset;
    uint64_t compose_record_count;
    uint64_t compose_records_offset;
    hash128_t ucd_hash;              /* BLAKE3-128 fingerprint of the UCDXML source */
    uint8_t  reserved[16];           /* pads the header to a true 128 B */
} laplace_perfcache_header_t;        /* total 128 B (two cache lines) */

/* The emitter writes the header by appending each field in declaration
 * order with no padding; the loader casts the mmap'd bytes straight to
 * this struct. Both rely on the struct having NO internal padding and the
 * exact documented sizes. Assert it so a field reorder / type change can
 * never silently shift section offsets (the off-by-8 that 120 vs 128
 * bytes would otherwise cause). */
#ifdef __cplusplus
static_assert(sizeof(laplace_perfcache_record_t) == 80, "perfcache record must be 80 bytes");
static_assert(sizeof(laplace_perfcache_header_t) == 128, "perfcache header must be 128 bytes");
#else
_Static_assert(sizeof(laplace_perfcache_record_t) == 80, "perfcache record must be 80 bytes");
_Static_assert(sizeof(laplace_perfcache_header_t) == 128, "perfcache header must be 128 bytes");
#endif

/* Trailer (16 bytes): BLAKE3-128 of everything from byte 0 up to (not
 * including) the trailer. Refuse to load on mismatch. */
#define LAPLACE_PERFCACHE_TRAILER_BYTES 16u

#ifdef __cplusplus
}
#endif
