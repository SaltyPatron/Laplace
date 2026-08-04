#pragma once

/*
 * Chess compose-floor blob — GH #822 / docs/specs/33_Perfcache_Blob_Law.md
 *
 * Deterministic lossless geometry ROM ABOVE tier 0 (codepoints stay t0 only):
 *   tier 1 — finite piece×square vocabulary (chess "graphemes/words")
 *   tier 2 — catalog positions composed from those units (openings/960/… as
 *            "sentences" that select which boards the ROM covers)
 *
 * Record: id → coord / hilbert / n / tier. No Glicko, attestations, or
 * observation counts. NOT Syzygy. NOT ECO-as-universe. NOT a managed
 * ConcurrentDictionary / File.ReadLines presented as the ROM.
 *
 * Emit peers ucd_tables_emit (native pack from declared catalog + t0).
 * Postgres remains SoR for testimony. Rebuild is one-way. Never seed DB
 * from this file.
 */

#include <stdint.h>
#include <stddef.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 'LCHP' little-endian */
#define LAPLACE_CHESS_PERFCACHE_MAGIC 0x5048434Cu
#define LAPLACE_CHESS_PERFCACHE_VERSION 1u
#define LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES 16u
#define LAPLACE_CHESS_PERFCACHE_RECORD_SIZE 80u
#define LAPLACE_CHESS_PERFCACHE_HEADER_SIZE 128u

/* Generator tag baked into source_hash inputs (emit side). */
#define LAPLACE_CHESS_PERFCACHE_GENERATOR_TAG "chess_position_perfcache/v1"

typedef struct {
    hash128_t    id;          /* 16 — position content id (tier 2 Merkle) */
    double       coord[4];    /* 32 */
    hilbert128_t hilbert;     /* 16 */
    uint32_t     n;           /*  4 — constituent count at compose */
    uint8_t      tier;        /*  1 — 1 = substructure vocab, 2 = position */
    uint8_t      _pad[3];     /*  3 */
    uint8_t      reserved[8]; /*  8 — forward compat; keep record 80 B */
} laplace_chess_perfcache_record_t;

typedef struct {
    uint32_t  magic;
    uint32_t  format_version;
    uint64_t  record_count;
    uint64_t  record_size;       /* must be LAPLACE_CHESS_PERFCACHE_RECORD_SIZE */
    uint64_t  records_offset;    /* typically 128 */
    hash128_t source_hash;       /* fingerprint of emit inputs + generator tag */
    char      scope[16];         /* e.g. "catalog\0" — ASCII, NUL-padded */
    uint8_t   reserved[64];      /* pad header to 128 B */
} laplace_chess_perfcache_header_t;

#ifdef __cplusplus
static_assert(sizeof(laplace_chess_perfcache_record_t) == 80,
              "chess perfcache record must be 80 bytes");
static_assert(sizeof(laplace_chess_perfcache_header_t) == 128,
              "chess perfcache header must be 128 bytes");
#else
_Static_assert(sizeof(laplace_chess_perfcache_record_t) == 80,
               "chess perfcache record must be 80 bytes");
_Static_assert(sizeof(laplace_chess_perfcache_header_t) == 128,
               "chess perfcache header must be 128 bytes");
#endif

#ifdef __cplusplus
}
#endif
