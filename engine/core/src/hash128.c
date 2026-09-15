#include "laplace/core/hash128.h"

#include <string.h>

#include "blake3.h"

static const uint8_t MERKLE_DOMAIN = 0x01;

void hash128_blake3(const uint8_t* data, size_t len, hash128_t* out) {
    blake3_hasher h;
    blake3_hasher_init(&h);
    if (data && len > 0) {
        blake3_hasher_update(&h, data, len);
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

void hash128_merkle(uint8_t tier, const hash128_t* children, size_t n, hash128_t* out) {
    /*
     * CONTENT-ADDRESSING LAW: same content = same hash. The id is a function
     * of the child-id sequence and nothing else — no tier, no ordinal, no
     * container. Tier is a FLOOR, not identity: entities.tier records the
     * lowest form of the content ('cat' is a tier-2 word that can stand as a
     * sentence on its own — "How do you feel?" → "Fine" — same id at every
     * tier above its floor; hash_composer collapses single-child nodes to the
     * child id for exactly this reason).
     */
    (void)tier;
    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));
    if (children && n > 0) {
        blake3_hasher_update(&h, children, n * sizeof(hash128_t));
    }
    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
}

int hash128_merkle_runs(size_t child_count, hash128_run_reader_t reader,
                        void* context, hash128_t* out) {
    if (!reader || !out || child_count < 2) return -1;

    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, &MERKLE_DOMAIN, sizeof(MERKLE_DOMAIN));

    size_t seen = 0;
    while (seen < child_count) {
        hash128_t child;
        size_t run = 0;
        int rc = reader(context, &child, &run);
        if (rc != 0 || run == 0 || run > child_count - seen) return -1;

        hash128_t block[64];
        for (size_t i = 0; i < 64; ++i) block[i] = child;
        size_t remaining = run;
        while (remaining > 0) {
            size_t take = remaining < 64 ? remaining : 64;
            blake3_hasher_update(&h, block, take * sizeof(hash128_t));
            remaining -= take;
        }
        seen += run;
    }

    hash128_t extra;
    size_t extra_run = 0;
    if (reader(context, &extra, &extra_run) != 1) return -1;

    blake3_hasher_finalize(&h, (uint8_t*)out, sizeof(*out));
    return 0;
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
