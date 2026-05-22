#include "laplace/core/mantissa.h"

/* Real implementation lands Chunk 1 Story 1.7 — bit-level mantissa packing
 * per ADR 0012. Round-trip lossless on the 21+ payload bits per FP64 coord
 * component. Stubs satisfy linkage. */

void mantissa_pack(double vertex[4], const double base[4], const mantissa_payload_t* p) {
    (void)base; (void)p;
    vertex[0] = 0; vertex[1] = 0; vertex[2] = 0; vertex[3] = 0;
}

void mantissa_unpack(const double vertex[4], mantissa_payload_t* out) {
    (void)vertex;
    if (out) { out->tier = 0; out->position = 0; out->hash_partial = 0; }
}
