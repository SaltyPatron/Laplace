/*
 * rle_encode.c — RLE encode for byte streams and hash arrays.
 */

#include "laplace_pg/rle.h"

#include <string.h>

size_t laplace_rle_encode_bytes(const uint8_t *in,
                                size_t         in_len,
                                uint8_t       *out_values,
                                int32_t       *out_counts)
{
    if (in_len == 0) {
        return 0;
    }
    size_t  runs    = 0;
    uint8_t current = in[0];
    int32_t count   = 1;
    for (size_t i = 1; i < in_len; ++i) {
        if (in[i] == current) {
            ++count;
        } else {
            out_values[runs] = current;
            out_counts[runs] = count;
            ++runs;
            current = in[i];
            count   = 1;
        }
    }
    out_values[runs] = current;
    out_counts[runs] = count;
    ++runs;
    return runs;
}

size_t laplace_rle_encode_hashes(const uint8_t *in_hashes,
                                 size_t         in_count,
                                 uint8_t       *out_hashes,
                                 int32_t       *out_counts)
{
    if (in_count == 0) {
        return 0;
    }
    size_t runs = 0;
    memcpy(out_hashes, in_hashes, LAPLACE_HASH_BYTES);
    int32_t count = 1;
    for (size_t i = 1; i < in_count; ++i) {
        const uint8_t *prev = out_hashes + (runs * LAPLACE_HASH_BYTES);
        const uint8_t *cur  = in_hashes  + (i    * LAPLACE_HASH_BYTES);
        if (memcmp(prev, cur, LAPLACE_HASH_BYTES) == 0) {
            ++count;
        } else {
            out_counts[runs] = count;
            ++runs;
            memcpy(out_hashes + (runs * LAPLACE_HASH_BYTES), cur, LAPLACE_HASH_BYTES);
            count = 1;
        }
    }
    out_counts[runs] = count;
    ++runs;
    return runs;
}
