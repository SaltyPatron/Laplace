/*
 * blake3_atom.c — atom hash (raw content bytes → 32-byte BLAKE3-256).
 */

#include "laplace_pg/hash.h"
#include "blake3.h"

void laplace_hash_atom(const uint8_t *content,
                       size_t         content_len,
                       uint8_t        out_hash[LAPLACE_HASH_BYTES])
{
    blake3_hasher h;
    blake3_hasher_init(&h);
    if (content_len > 0 && content != NULL) {
        blake3_hasher_update(&h, content, content_len);
    }
    blake3_hasher_finalize(&h, out_hash, LAPLACE_HASH_BYTES);
}
