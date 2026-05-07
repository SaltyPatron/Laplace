/*
 * rle.h — RleService public API.
 *
 * Phase 2 / Track B / Service B3.
 *
 * Run-length encoding for byte streams and 32-byte hash arrays. Used at
 * every composition tier so entities are referenced as FEW times as
 * physically possible.
 */

#ifndef LAPLACE_RLE_H
#define LAPLACE_RLE_H

#include <stddef.h>
#include <stdint.h>

#include "laplace_pg/hash.h"

#ifdef __cplusplus
extern "C" {
#endif

/* RLE-encode a byte sequence.
 *   in        : input byte sequence
 *   in_len    : length of input
 *   out_values: caller-allocated buffer of size >= in_len for unique values
 *   out_counts: caller-allocated buffer of size >= in_len for run lengths
 * Returns the number of (value, count) runs written.
 */
size_t laplace_rle_encode_bytes(const uint8_t *in,
                                size_t         in_len,
                                uint8_t       *out_values,
                                int32_t       *out_counts);

/* Decode RLE byte runs back into a flat byte sequence. */
size_t laplace_rle_decode_bytes(const uint8_t *values,
                                const int32_t *counts,
                                size_t         n_runs,
                                uint8_t       *out,
                                size_t         out_capacity);

/* RLE-encode a sequence of 32-byte hashes (used by composition_child rows).
 *   in_hashes : pointer to in_count consecutive 32-byte hashes
 *   in_count  : number of input hashes
 *   out_hashes: caller-allocated buffer of size >= in_count * 32 bytes
 *   out_counts: caller-allocated buffer of size >= in_count
 * Returns the number of (hash, count) runs written.
 */
size_t laplace_rle_encode_hashes(const uint8_t *in_hashes,
                                 size_t         in_count,
                                 uint8_t       *out_hashes,
                                 int32_t       *out_counts);

/* Decode RLE hash runs back into a flat hash sequence. */
size_t laplace_rle_decode_hashes(const uint8_t *hashes,
                                 const int32_t *counts,
                                 size_t         n_runs,
                                 uint8_t       *out_hashes,
                                 size_t         out_capacity_count);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_RLE_H */
