#pragma once

/*
 * Modality number perfcache v1 — a derived ROM for the CURRENT LEGACY
 * decimal-number media recipe. See docs/specs/33_Perfcache_Blob_Law.md.
 *
 * v1 materializes unsigned decimal integer roots 0..255 for O(1) lookup.
 * That fact does NOT define the ontology of image/audio physical samples and
 * does not require future media recipes to identify a sample with its decimal
 * Unicode spelling. docs/invention/modality-ladder-law.md and GH #1134 own the
 * corrected media representation. Regenerate/re-scope/retire this blob rather
 * than forcing a new recipe to preserve its legacy semantics.
 *
 * Never seed DB semantic authority from this file; it is rebuildable derived state.
 */

#include <stdint.h>
#include <stddef.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 'LMNP' little-endian — Laplace Modality Number Perfcache */
#define LAPLACE_MODALITY_NUMBER_PERFCACHE_MAGIC 0x504E4D4Cu
#define LAPLACE_MODALITY_NUMBER_PERFCACHE_VERSION 1u
#define LAPLACE_MODALITY_NUMBER_PERFCACHE_TRAILER_BYTES 16u
#define LAPLACE_MODALITY_NUMBER_PERFCACHE_RECORD_SIZE 80u
#define LAPLACE_MODALITY_NUMBER_PERFCACHE_HEADER_SIZE 128u

/* v1 dense table: every channel-byte magnitude. */
#define LAPLACE_MODALITY_NUMBER_PERFCACHE_VALUE_COUNT 256u

#define LAPLACE_MODALITY_NUMBER_PERFCACHE_GENERATOR_TAG "modality_number_perfcache/v1"
#define LAPLACE_MODALITY_NUMBER_PERFCACHE_SCOPE "u8"

typedef struct {
    hash128_t    id;          /* 16 — number entity id (text content root) */
    double       coord[4];    /* 32 */
    hilbert128_t hilbert;     /* 16 */
    uint32_t     value;       /*  4 — must equal dense index in v1 */
    uint32_t     n;           /*  4 — digit count in canonical decimal string */
    uint8_t      tier;        /*  1 — natural-unit tier from text decompose */
    uint8_t      _pad[7];     /*  7 — pad record to 80 B */
} laplace_modality_number_perfcache_record_t;

typedef struct {
    uint32_t  magic;
    uint32_t  format_version;
    uint64_t  record_count;      /* v1: 256 */
    uint64_t  record_size;       /* must be LAPLACE_MODALITY_NUMBER_PERFCACHE_RECORD_SIZE */
    uint64_t  records_offset;    /* typically 128 */
    hash128_t source_hash;       /* fingerprint of t0 + generator tag + scope */
    char      scope[16];         /* "u8\0" — dense channel-byte table */
    uint8_t   reserved[64];      /* pad header to 128 B */
} laplace_modality_number_perfcache_header_t;

#ifdef __cplusplus
static_assert(sizeof(laplace_modality_number_perfcache_record_t) == 80,
              "modality number perfcache record must be 80 bytes");
static_assert(sizeof(laplace_modality_number_perfcache_header_t) == 128,
              "modality number perfcache header must be 128 bytes");
#else
_Static_assert(sizeof(laplace_modality_number_perfcache_record_t) == 80,
               "modality number perfcache record must be 80 bytes");
_Static_assert(sizeof(laplace_modality_number_perfcache_header_t) == 128,
               "modality number perfcache header must be 128 bytes");
#endif

#ifdef __cplusplus
}
#endif
