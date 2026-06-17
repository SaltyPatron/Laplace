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







#define LAPLACE_VFLAG_TESTIMONY     (1ULL << 6)
#define LAPLACE_VFLAG_SCORE_SHIFT   7u
#define LAPLACE_VFLAG_SCORE_MASK    0xFFFFFFFFFULL   

static inline uint64_t laplace_vertex_flags(uint8_t tier, int has_atom, uint32_t atom) {
    uint64_t f = ((uint64_t)(tier & LAPLACE_VFLAG_TIER_MASK)) << LAPLACE_VFLAG_TIER_SHIFT;
    if (has_atom)
        f |= LAPLACE_VFLAG_HAS_ATOM
          |  ((uint64_t)(atom & LAPLACE_VFLAG_ATOM_MASK)) << LAPLACE_VFLAG_ATOM_SHIFT;
    return f;
}



static inline int laplace_vflag_has_atom(uint64_t flags) {
    return (flags & LAPLACE_VFLAG_HAS_ATOM) != 0;
}
static inline uint8_t laplace_vflag_tier(uint64_t flags) {
    return (uint8_t)((flags >> LAPLACE_VFLAG_TIER_SHIFT) & LAPLACE_VFLAG_TIER_MASK);
}
static inline uint32_t laplace_vflag_atom(uint64_t flags) {
    return (uint32_t)((flags >> LAPLACE_VFLAG_ATOM_SHIFT) & LAPLACE_VFLAG_ATOM_MASK);
}

void mantissa_pack(double vertex[4], const mantissa_payload_t* p);
void mantissa_unpack(const double vertex[4], mantissa_payload_t* out);





int laplace_testimony_pack_walk(const hash128_t* object_ids,
                                const int64_t*   scores_fp1e9,
                                const uint16_t*  games,
                                size_t n, double* out);



int laplace_testimony_unpack_vertex(const double vertex[4],
                                    hash128_t* object_id,
                                    int64_t*   score_fp1e9,
                                    uint16_t*  games,
                                    uint16_t*  ordinal);

#ifdef __cplusplus
}
#endif
