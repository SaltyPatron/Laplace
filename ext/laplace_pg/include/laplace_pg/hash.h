/*
 * hash.h — BLAKE3HashService public API.
 *
 * Phase 2 / Track B / Service B2.
 *
 * Three canonical hash functions, each producing the substrate's 32-byte
 * BLAKE3-256 identity:
 *   - atom        : hash of raw content bytes (tier-0 atoms)
 *   - composition : Merkle hash of ordered child hashes with RLE counts
 *                   (tier-1+ entities)
 *   - edge        : (edge_type_hash, role-ordered (role_hash, role_position,
 *                   participant_hash) triples) — edges are content-addressed
 *                   the same way entities are; edge type and role are
 *                   themselves substrate entities, NEVER hardcoded enums
 *
 * One implementation. NO managed-side fallback. Native + managed share via
 * P/Invoke; SQL extension functions wrap the same C symbols.
 */

#ifndef LAPLACE_HASH_H
#define LAPLACE_HASH_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_HASH_BYTES 32

/* Hash raw content bytes to a 32-byte BLAKE3-256 atom hash. */
void laplace_hash_atom(const uint8_t *content,
                       size_t         content_len,
                       uint8_t        out_hash[LAPLACE_HASH_BYTES]);

/*
 * Hash a Merkle composition of children with RLE counts.
 * child_hashes: pointer to n_children consecutive 32-byte hashes.
 * rle_counts:   pointer to n_children int32_t counts (parallel array).
 *
 * Hash construction: BLAKE3 over the concatenation of
 *   for i in 0..n_children:
 *     child_hashes[i] || little-endian u32 rle_counts[i]
 * This is deterministic, content-addressed, and order-sensitive.
 */
void laplace_hash_composition(const uint8_t *child_hashes,
                              const int32_t *rle_counts,
                              size_t         n_children,
                              uint8_t        out_hash[LAPLACE_HASH_BYTES]);

/*
 * Hash an edge.
 * edge_type_hash:  32 bytes, references the substrate entity that IS this edge type
 * role_hashes:     n_members * 32 bytes, role entity per member
 * role_positions:  n_members int32_t, position within the role (for n-ary edges)
 * participant_hashes: n_members * 32 bytes, the participating entity per member
 *
 * Construction: BLAKE3 over edge_type_hash followed by, for i in 0..n_members:
 *   role_hashes[i] || little-endian u32 role_positions[i] || participant_hashes[i]
 */
void laplace_hash_edge(const uint8_t *edge_type_hash,
                       const uint8_t *role_hashes,
                       const int32_t *role_positions,
                       const uint8_t *participant_hashes,
                       size_t         n_members,
                       uint8_t        out_hash[LAPLACE_HASH_BYTES]);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_HASH_H */
