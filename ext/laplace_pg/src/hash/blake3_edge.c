/*
 * blake3_edge.c — edge identity hash.
 *
 * Edge identity = BLAKE3 over edge_type_hash followed by, per member,
 *   role_hash || little-endian u32 role_position || participant_hash.
 *
 * Edge type and role are themselves substrate entities (compositions of
 * their codepoint LINESTRINGs) — NEVER hardcoded enum values. Same edge
 * type + same role-ordered participants = same hash = same edge row,
 * deduplicated across decomposers.
 */

#include "laplace_pg/hash.h"
#include "blake3.h"

void laplace_hash_edge(const uint8_t *edge_type_hash,
                       const uint8_t *role_hashes,
                       const int32_t *role_positions,
                       const uint8_t *participant_hashes,
                       size_t         n_members,
                       uint8_t        out_hash[LAPLACE_HASH_BYTES])
{
    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, edge_type_hash, LAPLACE_HASH_BYTES);

    for (size_t i = 0; i < n_members; ++i) {
        blake3_hasher_update(&h, role_hashes + (i * LAPLACE_HASH_BYTES), LAPLACE_HASH_BYTES);

        const uint32_t pos = (uint32_t) role_positions[i];
        const uint8_t  buf[4] = {
            (uint8_t)(pos        & 0xFFu),
            (uint8_t)((pos >>  8) & 0xFFu),
            (uint8_t)((pos >> 16) & 0xFFu),
            (uint8_t)((pos >> 24) & 0xFFu)
        };
        blake3_hasher_update(&h, buf, sizeof buf);

        blake3_hasher_update(&h, participant_hashes + (i * LAPLACE_HASH_BYTES), LAPLACE_HASH_BYTES);
    }
    blake3_hasher_finalize(&h, out_hash, LAPLACE_HASH_BYTES);
}
