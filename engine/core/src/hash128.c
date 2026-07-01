#include "laplace/core/hash128.h"

#include <string.h>

#include "blake3.h"

void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out) {
    blake3_hasher h;
    blake3_hasher_init(&h);
    if (data && len > 0) {
        blake3_hasher_update(&h, data, len);
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out) {
    static const uint8_t MERKLE_DOMAIN = 0x01;
    /*
     * Mix the tier byte into the domain-separated hash, right after the
     * MERKLE_DOMAIN byte. Previously `tier` was accepted but discarded
     * ((void)tier;), meaning composed-node hashing was tier-blind: the same
     * child-id sequence produced the same id regardless of which tier it was
     * being composed at. Collision risk was low in practice (ids are
     * effectively disjoint per tier via their own content), but this was
     * still a real correctness gap in a content-addressing system. Mixing
     * tier in is a hash-domain change: it changes ids for all tier>0
     * composed content, so any live deployment must pair this with a full
     * re-seed rather than a partial/incremental one.
     */
    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));
    blake3_hasher_update(&h, &tier, sizeof(tier));
    if (children && n > 0) {
        blake3_hasher_update(&h, children, n * sizeof(hash128_t));
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

int hash128_compare(const hash128_t* a, const hash128_t* b) {
    return memcmp(a, b, sizeof(hash128_t));
}

int hash128_equals(const hash128_t* a, const hash128_t* b) {
    return memcmp(a, b, sizeof(hash128_t)) == 0;
}

void hash128_zero(hash128_t* out) {
    memset(out, 0, sizeof(*out));
}
