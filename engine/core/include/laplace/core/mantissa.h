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
/* Ordinary content vertices historically carried a five-bit tier in bits 1-5.
 * Deep source composition needs all uint8 floors. Bits 43-46 are unused by
 * atom (bits 31-51), testimony (bits 6-42), and factor (bits 7-42) payloads;
 * the extension marker therefore leaves every legacy payload byte-for-byte.
 * It is emitted only for non-atom, non-special ordinary vertices at tier >31. */
#define LAPLACE_VFLAG_TIER_EXT_SHIFT 43u
#define LAPLACE_VFLAG_TIER_EXT_MASK  0x7ULL
#define LAPLACE_VFLAG_TIER_EXT       (1ULL << 46)
#define LAPLACE_VFLAG_ATOM_SHIFT    31u
#define LAPLACE_VFLAG_ATOM_MASK     0x1FFFFFULL







#define LAPLACE_VFLAG_TESTIMONY     (1ULL << 6)
#define LAPLACE_VFLAG_SCORE_SHIFT   7u
#define LAPLACE_VFLAG_SCORE_MASK    0xFFFFFFFFFULL

// FACTOR vertex class: raw float32 payload channel for per-circuit factor
// matrices (doc 26 item A). Discriminated by bit 7 with bits 0 and 6 clear —
// mutually exclusive with the atom and testimony classes, whose score/atom
// fields overlap these bit ranges only when their own class bit is set.
// Payload per vertex: 6 float32 = entity_id.lo (f0|f1), entity_id.hi (f2|f3),
// ordinal|run_length (f4), flags bits 8-39 (f5); bits 40-42 = valid count 1-6.
#define LAPLACE_VFLAG_FACTOR        (1ULL << 7)
#define LAPLACE_VFLAG_F5_SHIFT      8u
#define LAPLACE_VFLAG_F5_MASK       0xFFFFFFFFULL
#define LAPLACE_VFLAG_FCOUNT_SHIFT  40u
#define LAPLACE_VFLAG_FCOUNT_MASK   0x7ULL
#define LAPLACE_FACTOR_VALUES_PER_VERTEX 6u

static inline uint64_t laplace_vertex_flags(uint8_t tier, int has_atom, uint32_t atom) {
    uint64_t f = ((uint64_t)(tier & LAPLACE_VFLAG_TIER_MASK)) << LAPLACE_VFLAG_TIER_SHIFT;
    if (has_atom) {
        f |= LAPLACE_VFLAG_HAS_ATOM
          |  ((uint64_t)(atom & LAPLACE_VFLAG_ATOM_MASK)) << LAPLACE_VFLAG_ATOM_SHIFT;
    } else if (tier > LAPLACE_VFLAG_TIER_MASK) {
        f |= LAPLACE_VFLAG_TIER_EXT
          |  (((uint64_t)(tier >> 5) & LAPLACE_VFLAG_TIER_EXT_MASK)
              << LAPLACE_VFLAG_TIER_EXT_SHIFT);
    }
    return f;
}



static inline int laplace_vflag_has_atom(uint64_t flags) {
    return (flags & LAPLACE_VFLAG_HAS_ATOM) != 0;
}
static inline uint8_t laplace_vflag_tier(uint64_t flags) {
    uint8_t tier = (uint8_t)((flags >> LAPLACE_VFLAG_TIER_SHIFT) & LAPLACE_VFLAG_TIER_MASK);
    if ((flags & LAPLACE_VFLAG_TIER_EXT) != 0
        && (flags & (LAPLACE_VFLAG_HAS_ATOM | LAPLACE_VFLAG_TESTIMONY | LAPLACE_VFLAG_FACTOR)) == 0)
        tier |= (uint8_t)(((flags >> LAPLACE_VFLAG_TIER_EXT_SHIFT)
                           & LAPLACE_VFLAG_TIER_EXT_MASK) << 5);
    return tier;
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



int laplace_factor_pack_values(const float* values, size_t n,
                               double* out, size_t* out_vertices);



int laplace_factor_unpack_vertex(const double vertex[4],
                                 float out_values[6], uint8_t* out_count);

#ifdef __cplusplus
}
#endif
