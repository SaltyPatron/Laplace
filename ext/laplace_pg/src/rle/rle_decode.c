/*
 * rle_decode.c — RLE decode for byte streams and hash arrays.
 */

#include "laplace_pg/rle.h"

#include <string.h>

size_t laplace_rle_decode_bytes(const uint8_t *values,
                                const int32_t *counts,
                                size_t         n_runs,
                                uint8_t       *out,
                                size_t         out_capacity)
{
    size_t written = 0;
    for (size_t i = 0; i < n_runs; ++i) {
        const int32_t c = counts[i];
        if (c <= 0) {
            continue;
        }
        if (written + (size_t) c > out_capacity) {
            return written; /* truncate to capacity */
        }
        memset(out + written, values[i], (size_t) c);
        written += (size_t) c;
    }
    return written;
}

size_t laplace_rle_decode_hashes(const uint8_t *hashes,
                                 const int32_t *counts,
                                 size_t         n_runs,
                                 uint8_t       *out_hashes,
                                 size_t         out_capacity_count)
{
    size_t written = 0;
    for (size_t i = 0; i < n_runs; ++i) {
        const int32_t c = counts[i];
        if (c <= 0) {
            continue;
        }
        if (written + (size_t) c > out_capacity_count) {
            return written;
        }
        const uint8_t *src = hashes + (i * LAPLACE_HASH_BYTES);
        for (int32_t j = 0; j < c; ++j) {
            memcpy(out_hashes + ((written + (size_t) j) * LAPLACE_HASH_BYTES),
                   src,
                   LAPLACE_HASH_BYTES);
        }
        written += (size_t) c;
    }
    return written;
}
