#pragma once

#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    hash128_t entity_id;
    uint16_t  ordinal;
    uint16_t  run_length;
    uint64_t  flags;
} mantissa_payload_t;

#define LAPLACE_VFLAG_HAS_ATOM      (1ULL << 0)
#define LAPLACE_VFLAG_TIER_SHIFT    1u
#define LAPLACE_VFLAG_TIER_MASK     0x1FULL
#define LAPLACE_VFLAG_ATOM_SHIFT    31u
#define LAPLACE_VFLAG_ATOM_MASK     0x1FFFFFULL

static inline uint64_t laplace_vertex_flags(uint8_t tier, int has_atom, uint32_t atom) {
    uint64_t f = ((uint64_t)(tier & LAPLACE_VFLAG_TIER_MASK)) << LAPLACE_VFLAG_TIER_SHIFT;
    if (has_atom)
        f |= LAPLACE_VFLAG_HAS_ATOM
          |  ((uint64_t)(atom & LAPLACE_VFLAG_ATOM_MASK)) << LAPLACE_VFLAG_ATOM_SHIFT;
    return f;
}

void mantissa_pack(double vertex[4], const mantissa_payload_t* p);
void mantissa_unpack(const double vertex[4], mantissa_payload_t* out);

#ifdef __cplusplus
}
#endif
