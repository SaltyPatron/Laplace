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
int  hash128_compare(const hash128_t* a, const hash128_t* b);
int  hash128_equals(const hash128_t* a, const hash128_t* b);
void hash128_zero(hash128_t* out);

/* BLAKE3 of a NUL-terminated string's bytes (sans the NUL). The one canonical
 * string hash -- callers minting a content id from a label share this instead
 * of re-wrapping strlen+blake3. */
static inline void hash128_blake3_str(const char* s, hash128_t* out) {
    hash128_blake3((const uint8_t*)s, strlen(s), out);
}

#ifdef __cplusplus
}
#endif
