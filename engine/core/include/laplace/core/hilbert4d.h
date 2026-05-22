#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 4D Hilbert curve index — a single 1D sortable value with locality
 * preserved across the [-1, 1]^4 bounding hyperbox (per ADR 0005).
 *
 * One Hilbert curve indexes both S³ surface entities and 4-ball
 * interior centroids, giving B-tree range scans consistent spatial
 * locality across the full 4D box.
 *
 * Read patterns:
 *   - PG B-tree on `hilbert_index bytea` (server-side memcmp; no C layout dep)
 *   - C-side `hilbert128_compare` (memcmp on bytes)
 *   - Skilling 2004 encode/decode (algorithm-internal; bytes are sufficient
 *     at the API boundary — see Chunk 1 Story 1.5 for the impl).
 *
 * Layout: 16 raw bytes, matching PG `bytea(16)` storage. No {hi, lo} split
 * at this level — the split would only be a micro-optimization for the
 * encode/decode internals, and that decision belongs inside those
 * functions, not the public ABI. */
typedef struct {
    uint8_t bytes[16];
} hilbert128_t;

/* Implementations land Chunk 1 Story 1.5 — Skilling 2004 algorithm.
 * Pure integer bit-twiddling; no FP. */
void hilbert4d_encode(const double p[4], hilbert128_t* out);
void hilbert4d_decode(const hilbert128_t* h, double out[4]);
int  hilbert128_compare(const hilbert128_t* a, const hilbert128_t* b);

#ifdef __cplusplus
}
#endif
