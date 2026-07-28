#pragma once

#include <stdint.h>
#include <stddef.h>

#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Tier-0 atom alphabets for the non-text modalities.
 *
 * docs/invention/modality-ladder-law.md, "the one law (every modality)":
 *   "Tier-0 = quantized scalar alphabet with a canonical total order. Mints
 *    through the scalar content law; S3-anchored by that order."
 *
 * This module owns the ORDER and the GEOMETRY halves of that law — the two
 * parts the spec fully determines. It deliberately does NOT mint ids, and the
 * reason is a trap worth recording.
 *
 * The law says tier-0 "mints through the scalar content law". That phrase must
 * NOT be read as ModelCoordinates.ScalarId — the content root of the decimal
 * string. Under that reading the PCM sample 500 would be the composition of the
 * codepoints '5','0','0', which makes it tier 1 AT LOWEST and puts it on the
 * TEXT ladder: the exact opposite of "tier-0 quantized alphabet" and of "each
 * modality gets its own ladder under the same law".
 *
 * ScalarId is right for what it was built for — layer and head INDICES, numbers
 * a person writes down, which genuinely are text ("14" here must BE "14"
 * everywhere). A PCM sample and a packed RGB triple are not written numbers;
 * they are this modality's codepoints. So their identity has to come from their
 * own canonical bytes, the way a codepoint's does (blake3 of its UTF-8 form in
 * unicode_seed.cpp) — never from a decimal rendering of them.
 *
 * Minting is therefore left to the seeder that builds each alphabet's blob, and
 * this module stays the part that is unambiguous.
 *
 * Text is the reference instance and lives in codepoint_table.c: UCA order,
 * S3 anchor by that order, dense perfcache. The difference for image and audio
 * is that their canonical order is closed-form rather than table-driven — no
 * UCD-equivalent to load, so rank is arithmetic on the atom itself, and the
 * geometry is a single super-Fibonacci point rather than a materialised table.
 * That matters for image: 2^24 atoms would be 512 MiB of coordinates.
 */
typedef enum {
    /*
     * Packed RGB, ROCK-LOCKED order: (R<<16)|(G<<8)|B, so the packed value IS
     * the rank. The law permits an operator override BEFORE the first image
     * seed and never after; changing it later reassigns every image atom's S3
     * anchor and invalidates every deposited image trajectory.
     */
    LAPLACE_MODALITY_IMAGE = 1,

    /*
     * 16-bit PCM in amplitude order: 65,536 atoms, rank = sample + 32768, so
     * the most negative excursion is rank 0 and the order is monotone in
     * amplitude. Channel is a PARTITION, not a tier (law, audio section), so it
     * never enters the alphabet.
     */
    LAPLACE_MODALITY_AUDIO = 2,
} laplace_modality_t;

/* Alphabet cardinality, or 0 for an unknown modality. */
uint64_t laplace_modality_alphabet_size(laplace_modality_t modality);

/*
 * Canonical total-order rank of an atom in [0, alphabet_size).
 * Returns 0 on success, -1 if the modality is unknown or the atom is outside
 * its alphabet. Atoms arrive as int64 so the signed PCM range is representable
 * without a per-modality signature.
 */
int laplace_modality_atom_rank(laplace_modality_t modality, int64_t atom,
                               uint64_t* out_rank);

/* Inverse of laplace_modality_atom_rank. */
int laplace_modality_atom_from_rank(laplace_modality_t modality, uint64_t rank,
                                    int64_t* out_atom);

/*
 * The S3 anchor and hilbert index an atom takes from its canonical order —
 * the same construction unicode_seed.cpp applies to codepoints, evaluated for
 * one atom instead of materialised for the whole alphabet.
 */
int laplace_modality_atom_geometry(laplace_modality_t modality, int64_t atom,
                                   uint64_t* out_rank, double out_coord[4],
                                   hilbert128_t* out_hilbert);

/* Packed-RGB helpers for the image alphabet (the rock-locked order). */
static inline uint32_t laplace_image_atom_pack(uint8_t r, uint8_t g, uint8_t b) {
    return ((uint32_t)r << 16) | ((uint32_t)g << 8) | (uint32_t)b;
}
static inline void laplace_image_atom_unpack(uint32_t atom, uint8_t* r, uint8_t* g, uint8_t* b) {
    if (r) *r = (uint8_t)((atom >> 16) & 0xFFu);
    if (g) *g = (uint8_t)((atom >> 8) & 0xFFu);
    if (b) *b = (uint8_t)(atom & 0xFFu);
}

#ifdef __cplusplus
}
#endif
