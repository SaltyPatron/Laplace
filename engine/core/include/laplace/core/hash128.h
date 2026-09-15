#pragma once

#include <stdint.h>
#include <stddef.h>
#include <string.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint64_t hi;
    uint64_t lo;
} hash128_t;

void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out);
void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out);

/* Allocation-free peer of hash128_merkle for RLE/streamed child manifests.
 * reader returns 0 with one (child,run) pair, 1 at end, negative on failure.
 * child_count is the exact expanded count and must be >= 2. The resulting bytes
 * are bit-identical to hash128_merkle over the fully expanded child array. */
typedef int (*hash128_run_reader_t)(void* context, hash128_t* child, size_t* run);
int hash128_merkle_runs(size_t child_count, hash128_run_reader_t reader,
                        void* context, hash128_t* out);

int  hash128_compare(const hash128_t* a, const hash128_t* b);
int  hash128_equals(const hash128_t* a, const hash128_t* b);
void hash128_zero(hash128_t* out);

static inline void hash128_blake3_str(const char* s, hash128_t* out) {
    hash128_blake3((const uint8_t*)s, strlen(s), out);
}

#ifdef __cplusplus
}
#endif
