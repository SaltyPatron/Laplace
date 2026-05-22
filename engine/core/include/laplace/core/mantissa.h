#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Mantissa-packed payload riding in low bits of FP64 coordinate components
 * (per ADR 0012). Operates on XYZM-packed double buffers — matches POINT4D
 * memory layout (no parallel datatype per RULES.md R1). */
typedef struct {
    uint8_t  tier;
    uint16_t position;
    uint64_t hash_partial;
} mantissa_payload_t;

/* Implementations land in Chunk 1 Story 1.7. Round-trip lossless on
 * payload bits. */
void mantissa_pack(double vertex[4], const double base[4], const mantissa_payload_t* p);
void mantissa_unpack(const double vertex[4], mantissa_payload_t* out);

#ifdef __cplusplus
}
#endif
