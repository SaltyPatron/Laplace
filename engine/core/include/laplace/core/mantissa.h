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

/* Testimony vertices ride the SAME 212-bit law: the walk's object reference in
 * X/Y/Z, games in run_length (RLE: a repeated observation IS a longer run),
 * ordinal = position, and the zigzagged fp1e9 score in the flags field. The
 * TESTIMONY bit discriminates from content vertices (which use HAS_ATOM/tier/
 * atom); the score field overlays the atom range — lawful because the two
 * vertex kinds never mix flags. */
#define LAPLACE_VFLAG_TESTIMONY     (1ULL << 6)
#define LAPLACE_VFLAG_SCORE_SHIFT   7u
#define LAPLACE_VFLAG_SCORE_MASK    0xFFFFFFFFFULL   /* 36 bits zigzag */

static inline uint64_t laplace_vertex_flags(uint8_t tier, int has_atom, uint32_t atom) {
    uint64_t f = ((uint64_t)(tier & LAPLACE_VFLAG_TIER_MASK)) << LAPLACE_VFLAG_TIER_SHIFT;
    if (has_atom)
        f |= LAPLACE_VFLAG_HAS_ATOM
          |  ((uint64_t)(atom & LAPLACE_VFLAG_ATOM_MASK)) << LAPLACE_VFLAG_ATOM_SHIFT;
    return f;
}

void mantissa_pack(double vertex[4], const mantissa_payload_t* p);
void mantissa_unpack(const double vertex[4], mantissa_payload_t* out);

/* One testimony walk packed in a single call: n object references with
 * per-vertex score and games. out must hold 4*n doubles. Returns 0, or -1 on
 * bad args, -2 if a score exceeds the 36-bit zigzag budget (|score| must stay
 * under 2^35 fp1e9 — thresholded table reads are within ±2e9). */
int laplace_testimony_pack_walk(const hash128_t* object_ids,
                                const int64_t*   scores_fp1e9,
                                const uint16_t*  games,
                                size_t n, double* out);

/* Inverse of one packed testimony vertex. Returns 0, or -1 if the vertex does
 * not carry the TESTIMONY flag. */
int laplace_testimony_unpack_vertex(const double vertex[4],
                                    hash128_t* object_id,
                                    int64_t*   score_fp1e9,
                                    uint16_t*  games,
                                    uint16_t*  ordinal);

#ifdef __cplusplus
}
#endif
