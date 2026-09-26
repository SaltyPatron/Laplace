#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * PostgreSQL's HASH partition law for a one-column bytea key, computed where rows
 * are packed. hashbyteaextended is hash_bytes_extended over the key's bytes with
 * the seed; a partitioned table combines that into a zero row hash with the
 * partition seed and owns the row by the combined hash modulo the table's
 * modulus. A writer that routes rows by this law hands each backend whole leaves.
 */
uint64_t laplace_pg_hash_bytes_extended(const uint8_t* key, size_t len, uint64_t seed);

/* Remainder of each 16-byte key (n keys packed contiguously) under `modulus`. */
void laplace_pg_hash_partition16(const uint8_t* keys, size_t n, uint32_t modulus,
                                 uint32_t* remainders);

#ifdef __cplusplus
}
#endif
