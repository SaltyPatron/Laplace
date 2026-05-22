#include "laplace/core/hilbert4d.h"

#include <string.h>

/* Real implementation lands Chunk 1 Story 1.5 — Skilling 2004 4D Hilbert
 * encode/decode over the [-1, 1]^4 bounding hyperbox (per ADR 0005). Pure
 * integer bit-twiddling; no FP. The algorithm-internal representation
 * (e.g., whether to use {uint64_t hi, lo} for the bit interleave) is an
 * implementation detail; the public ABI is bytes only.
 *
 * Stubs satisfy linkage. */

void hilbert4d_encode(const double p[4], hilbert128_t* out) {
    (void)p;
    if (out) memset(out->bytes, 0, sizeof(out->bytes));
}

void hilbert4d_decode(const hilbert128_t* h, double out[4]) {
    (void)h;
    out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 0;
}

int hilbert128_compare(const hilbert128_t* a, const hilbert128_t* b) {
    if (!a || !b) return 0;
    return memcmp(a->bytes, b->bytes, sizeof(a->bytes));
}
