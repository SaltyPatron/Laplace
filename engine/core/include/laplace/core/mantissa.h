#pragma once

#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Mantissa-packed payload riding inside a 4D LINESTRING vertex's FP64
 * components. Each vertex of an entity's `trajectory` column references
 * another entity (the one playing the constituent role at this vertex's
 * position). Same hash space as `entities.hash` — one identity, two roles
 * (per the "Entity" definition: content AND building block, simultaneously).
 *
 * Per-vertex 212-bit budget = 4 × (1 sign + 52 mantissa) per FP64 component,
 * with the biased exponent pinned to 0x3FF so every coord is a finite, normal
 * double of magnitude in [1, 2) — PG-geometry-valid, no NaN, no inf.
 *
 * Conceptual split: XYZ carries the referenced entity's ID; M carries the
 * per-vertex metadata of how this entity occupies this position.
 *
 *   X (53b): hash bits   0..52      (low 53 of entity_id.lo)
 *   Y (53b): hash bits  53..105     (high 11 of lo + low 42 of hi)
 *   Z (53b): hash bits 106..127     (high 22 of hi) + flags[0..30]   (31)
 *   M (53b): ordinal (16) + run_length (16) +        flags[31..51]   (21)
 *
 * Consequences of the layout:
 *   - The 128-bit BLAKE3 entity ID round-trips exactly through XYZ
 * (no truncation — the hash discipline).
 *   - Same entity → same XYZ bit pattern → same 3D scatter position across
 *     every trajectory that references it. Directly visualizable.
 *   - M is a scalar metadata channel (ordinal, run_length, plus 21 free flag
 *     bits) — usable for filtering / labeling / coloring without re-layout.
 *   - 52 free flag bits (31 in Z's high half + 21 in M's high half) reserved
 *     for modality, indexing, visualization markers, continuation, etc.
 *   - Trajectory vertices have NO per-vertex spatial meaning (every coord is
 *     in [1, 2) ∪ (-2, -1]). GIST-on-trajectory therefore filters nothing;
 *     structural indexing must be done via separate columns/opclasses.
 *
 * No `base` parameter: trajectory vertices are pure metadata containers, not
 * spatial points being annotated. */
typedef struct {
    hash128_t entity_id;   /* 128 bits — full BLAKE3-128, no truncation */
    uint16_t  ordinal;       /* position in this trajectory's vertex sequence */
    uint16_t  run_length;    /* RLE count of consecutive identical entities */
    uint64_t  flags;         /* low 52 bits used; high 12 MUST be zero */
} mantissa_payload_t;

/* Vertex flag layout (the 52 free bits; 2026-06-05 — in-band constituent
 * TYPE + ATOM so trajectory walks never join entities/codepoint_render to
 * know what a constituent IS):
 *
 *   bit  0       HAS_ATOM — bits 31..51 carry the constituent's atom scalar
 *   bits 1..5    constituent TIER (0=atom, 1=grapheme, 2=word, …; 31 max)
 *   bits 6..30   reserved (modality, indexing, visualization — zero)
 *   bits 31..51  atom scalar when HAS_ATOM (21 bits = a full Unicode
 *                codepoint U+0000..U+10FFFF; byte atoms / pixels use the
 *                same channel under their own tier)
 *
 * flags == 0 (legacy trajectories) means "no in-band info" — readers fall
 * back to id resolution. Renderers MUST honor HAS_ATOM before trusting the
 * atom channel. */
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

/* Round-trip lossless on all 212 payload bits. */
void mantissa_pack(double vertex[4], const mantissa_payload_t* p);
void mantissa_unpack(const double vertex[4], mantissa_payload_t* out);

#ifdef __cplusplus
}
#endif
