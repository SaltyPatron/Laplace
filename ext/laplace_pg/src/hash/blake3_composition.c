/*
 * blake3_composition.c — Merkle hash for tier-1+ composition entities.
 *
 * Children are concatenated in order, each followed by its little-endian u32
 * RLE count. RLE means same content adjacent shows up as one row with
 * rle_count > 1, never as multiple rows — entities are referenced as FEW
 * times as physically possible.
 */

#include "laplace_pg/hash.h"
#include "blake3.h"

#include <string.h>

void laplace_hash_composition(const uint8_t *child_hashes,
                              const int32_t *rle_counts,
                              size_t         n_children,
                              uint8_t        out_hash[LAPLACE_HASH_BYTES])
{
    blake3_hasher h;
    blake3_hasher_init(&h);
    for (size_t i = 0; i < n_children; ++i) {
        blake3_hasher_update(&h, child_hashes + (i * LAPLACE_HASH_BYTES), LAPLACE_HASH_BYTES);

        /* serialize rle_count as little-endian u32 (BLAKE3 input is byte-oriented) */
        const uint32_t c = (uint32_t) rle_counts[i];
        const uint8_t  buf[4] = {
            (uint8_t)(c        & 0xFFu),
            (uint8_t)((c >>  8) & 0xFFu),
            (uint8_t)((c >> 16) & 0xFFu),
            (uint8_t)((c >> 24) & 0xFFu)
        };
        blake3_hasher_update(&h, buf, sizeof buf);
    }
    blake3_hasher_finalize(&h, out_hash, LAPLACE_HASH_BYTES);
}
