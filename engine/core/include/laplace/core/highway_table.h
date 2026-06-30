#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ── On-disk structures (mmap'd binary) ──────────────────────────────────────
 * Binary layout: header(128) | rel_records(N×32) | band_masks(B×32) | strings
 * All values little-endian.  Do not change struct layout; bump format_version. */

typedef struct {
    uint32_t magic;
    uint32_t format_version;
    uint64_t relation_count;
    uint64_t relations_offset;
    uint64_t band_count;
    uint64_t band_masks_offset;
    uint64_t strings_offset;
    uint64_t strings_length;
    uint8_t  fingerprint[8];
    uint8_t  reserved[64];
} laplace_highway_header_t;     /* 128 bytes */

/* On-disk relation record — 32 bytes, no compiler padding (layout verified in highway_table.c).
 * Binary layout: '<IBBBBfh18x' (little-endian, no alignment padding). */
typedef struct {
    uint32_t name_off;      /* byte offset of canonical name in string section */
    uint8_t  name_len;      /* byte length of canonical name (excluding NUL) */
    uint8_t  rank_band;     /* HIGHWAY_BAND_* constant */
    uint8_t  bit_pos;       /* bit index in the 256-bit highway mask */
    uint8_t  symmetry;      /* 0 = asymmetric, 1 = symmetric */
    float    rank;          /* numeric rank [0.05, 1.0] */
    int16_t  parent_bit;    /* bit_pos of parent relation, -1 if none */
    uint8_t  _pad[18];
} laplace_highway_rel_rec_t;    /* 32 bytes */

/* 256-bit highway mask: four little-endian 64-bit words.
 * Bit N is set when a relation with bit_pos == N participates in an edge.
 * Test: (w[N/64] >> (N%64)) & 1.  OR/AND operate word-by-word. */
typedef struct { uint64_t w[4]; } laplace_mask256_t;   /* 32 bytes */

/* ── Lifecycle ────────────────────────────────────────────────────────────── */

/* Load and mmap the highway perfcache binary.
 * Returns 0 on success, -1 if path is NULL or file unreadable,
 * -2 if magic/version mismatch, -3 if binary is truncated/corrupt. */
int  highway_table_load(const char* path);

/* Unmap and reset.  Safe to call when not loaded. */
void highway_table_unload(void);

/* Non-zero if a perfcache is currently loaded. */
int  highway_table_is_loaded(void);

/* ── Lookups ──────────────────────────────────────────────────────────────── */

/* Look up by type_id (blake3(canonical_name)).
 * Returns 0 on hit; fills *out_bit_pos / *out_rank / *out_band (NULLs ok).
 * Returns -1 if not loaded or not found. */
int highway_table_relation_by_hash(const hash128_t* type_id,
                                   uint8_t*         out_bit_pos,
                                   float*           out_rank,
                                   uint8_t*         out_band);

/* Look up by bit position (0 … relation_count−1).
 * *out_canonical points into the mmap'd string section — valid while loaded.
 * Returns 0 on hit, -1 otherwise. */
int highway_table_relation_by_bit(uint8_t      bit_pos,
                                  const char** out_canonical,
                                  float*       out_rank,
                                  uint8_t*     out_band);

/* Retrieve the precomputed 256-bit mask for a band.
 * Returns 0 on success, -1 if not loaded or band out of range. */
int highway_table_band_mask(uint8_t band, laplace_mask256_t* out_mask);

/* ── Mask utilities ────────────────────────────────────────────────────────── */
laplace_mask256_t highway_table_mask_or (laplace_mask256_t a, laplace_mask256_t b);
laplace_mask256_t highway_table_mask_and(laplace_mask256_t a, laplace_mask256_t b);
/* Returns 1 if bit is set, 0 otherwise (or if m is NULL / bit ≥ 256). */
int               highway_table_mask_test(const laplace_mask256_t* m, uint8_t bit);
/* Set bit in mask in-place. */
void              highway_table_mask_set (laplace_mask256_t* m, uint8_t bit);
/* Non-zero if mask has any bit set (i.e. mask != 0). */
int               highway_table_mask_any(const laplace_mask256_t* m);

#ifdef __cplusplus
}
#endif
