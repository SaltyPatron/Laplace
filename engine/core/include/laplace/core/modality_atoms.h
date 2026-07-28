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
 * This module supplies the canonical ORDER and the geometry that follows from it.
 * It does not mint: what "the scalar content law" resolves to is not settled here,
 * and guessing an atom's canonical bytes would fix every id in the modality
 * forever.
 *
 * THREE DISTINCT THINGS, easy to blur and wrong to:
 *   identity   the content hash. Exact, and the only identity there is.
 *   coordinate derived deterministically FROM content — tier-0 by canonical order
 *              (super_fibonacci), above that the centroid of constituents. Same
 *              content -> same hash -> same coordinate, always.
 *   hilbert    NOT the coordinate. A quantized, locality-preserving linearization
 *              OVER coordinate space, for range scans and KNN pruning. It collides
 *              by construction: many coordinates share a cell.
 *
 * So neither coordinate nor hilbert is an identity, for two different reasons —
 * centroids collide (Rule #1, content_witness_batch.c: "centroids collide, e.g.
 * cat/act") and hilbert cells collide (quantization). Only the hash identifies.
 *
 * Text is the reference instance, in codepoint_table.c: canonical order, S3 anchor
 * by that order, dense perfcache. Image and audio differ only in that their order
 * is arithmetic on the atom rather than table-driven — there is no UCD-equivalent
 * to load.
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
