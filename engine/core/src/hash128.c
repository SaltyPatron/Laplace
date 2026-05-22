#include "laplace/core/hash128.h"

#include <string.h>

/* Real implementation (Chunk 1 Story 1.3) wraps BLAKE3 official C impl —
 * blake3_hasher_init + blake3_hasher_update + blake3_hasher_finalize, then
 * truncate to 128 bits via memcpy into hash128_t. The stubs preserve the
 * C ABI; bodies are placeholder. */

void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out) {
    (void)data; (void)len;
    if (out) { out->hi = 0; out->lo = 0; }
}

void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out) {
    (void)tier; (void)children; (void)n;
    if (out) { out->hi = 0; out->lo = 0; }
}

int hash128_compare(const hash128_t* a, const hash128_t* b) {
    if (!a || !b) return 0;
    if (a->hi != b->hi) return (a->hi < b->hi) ? -1 : 1;
    if (a->lo != b->lo) return (a->lo < b->lo) ? -1 : 1;
    return 0;
}

int hash128_equals(const hash128_t* a, const hash128_t* b) {
    return hash128_compare(a, b) == 0;
}

void hash128_zero(hash128_t* out) {
    if (out) { out->hi = 0; out->lo = 0; }
}
