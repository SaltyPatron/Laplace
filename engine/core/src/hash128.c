#include "laplace/core/hash128.h"

#include <string.h>

#include "blake3.h"

/* hash128_t in-memory layout MUST match `bytea(16)` so the PG opclass
 * `laplace_btree_hash128_ops` (Story 1.14) and the engine compare yield
 * identical orderings. The struct's `hi`/`lo` fields exist for hot-path
 * SIMD/access (per the header comment); the canonical ordering is byte
 * lexicographic, identical to memcmp on a 16-byte bytea. */

void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out) {
    blake3_hasher h;
    blake3_hasher_init(&h);
    if (data && len > 0) {
        blake3_hasher_update(&h, data, len);
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out) {
    /* Domain-separated Merkle composition: a CONSTANT composition-domain byte ||
     * children in given order. Order is preserved (children of an entity have
     * meaningful position — e.g., trajectory vertex order); we do not sort.
     *
     * TIER IS METADATA, NEVER IDENTITY: the same ordered constituent set
     * composed "at" two different strata is ONE entity (content is identity;
     * tier records decomposition depth and lives on the entity row, not in the
     * hash). The `tier` parameter is retained at call sites as that metadata
     * but contributes NOTHING to the id. The constant domain byte keeps
     * compositions collision-separated from raw leaf hashes. */
    static const uint8_t MERKLE_DOMAIN = 0x01;
    (void)tier;
    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));
    if (children && n > 0) {
        blake3_hasher_update(&h, children, n * sizeof(hash128_t));
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

int hash128_compare(const hash128_t* a, const hash128_t* b) {
    /* Byte-lexicographic order — identical to memcmp on the bytea(16)
     * representation, so PG btree on bytea and engine compare agree. */
    return memcmp(a, b, sizeof(hash128_t));
}

int hash128_equals(const hash128_t* a, const hash128_t* b) {
    return memcmp(a, b, sizeof(hash128_t)) == 0;
}

void hash128_zero(hash128_t* out) {
    memset(out, 0, sizeof(*out));
}
