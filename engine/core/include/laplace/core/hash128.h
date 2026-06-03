#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* BLAKE3 truncated to 128 bits — content-addressable entity identifier
 * (the standards). Stored as bytea(16) in Postgres; byte[16]
 * in C# via [StructLayout(Sequential)].
 *
 * Read patterns that justify the {hi, lo} split (NOT a vanity wrapper):
 *   - Mantissa pack: hi and lo go into DIFFERENT coordinate
 *     mantissas at write time; unpack reverses that at every cascade read.
 *     A `uint8_t[16]` layout would force byte-reconstruction at every
 *     read/write site — `{uint64_t hi, lo}` is one field access per half.
 *   - B-tree `laplace_btree_hash128_ops` compare: two `uint64_t` compares
 *     instead of memcmp loop expression.
 *   - SIMD ops on the hot read paths: 16-byte aligned struct with two
 *     `uint64_t` lanes maps to `__m128i` (`_mm_cmpeq_epi64`, etc.).
 *   - PG boundary cast: `(hash128_t*)VARDATA(bytea_arg)` is zero-cost
 *     because POD + 16 bytes + matching alignment.
 *
 * The struct exists because of substrate read patterns, not naming. */
typedef struct {
    uint64_t hi;
    uint64_t lo;
} hash128_t;

/* Implementations land in Chunk 1 Story 1.3 — wrap BLAKE3 official C impl. */
void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out);
void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out);
int  hash128_compare(const hash128_t* a, const hash128_t* b);
int  hash128_equals(const hash128_t* a, const hash128_t* b);
void hash128_zero(hash128_t* out);

#ifdef __cplusplus
}
#endif
