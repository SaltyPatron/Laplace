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
 * string. Under that reading the PCM sample 500 becomes the composition of the
 * codepoints '5','0','0': it would have CONSTITUENTS, and a thing with
 * constituents is not an atom. The audio ladder would then have no floor of its
 * own — its lowest rung would decompose into somebody else's content.
 *
 * The ladders are PARALLEL AND COMPLETE, not subtrees of text. Image runs
 * channel-value -> pixel -> patch -> region -> image; audio runs sample ->
 * window/frame -> onset segment -> phrase -> track; chess runs square/piece ->
 * resolved move -> position -> game; text runs codepoint -> grapheme -> word ->
 * sentence -> document. Tier numbers are modality-relative depths, not one
 * global scale, so "tier 0" means "this ladder's irreducible alphabet" and each
 * modality has to supply its own.
 *
 * ScalarId is right for what it was built for — layer and head INDICES, numbers
 * a person writes down, which genuinely are text ("14" here must BE "14"
 * everywhere). A PCM sample and a packed RGB triple are not written numbers;
 * they are this ladder's floor. So their identity has to come from their own
 * canonical bytes, the way a codepoint's does (blake3 of its UTF-8 form in
 * unicode_seed.cpp) — never from a decimal rendering of them.
 *
 * Minting is therefore left to the seeder that builds each alphabet's blob, and
 * this module stays the part that is unambiguous.
 *
 * Text is the reference instance and lives in codepoint_table.c: UCA order,
 * S3 anchor by that order, dense perfcache. The difference for image and audio
 * is only that their canonical order is closed-form rather than table-driven —
 * there is no UCD-equivalent to load, so rank is arithmetic on the atom itself.
 *
 * THE CLOSED FORM IS AN INTERIM, AND THE BLOB IS THE POINT. An earlier revision
 * of this comment claimed the image alphabet "cannot be materialised" because
 * 2^24 atoms is ~1.3 GiB of records, and treated that as a cost to be argued
 * around. That inverts the trade.
 *
 * The alphabet is a FIXED, ONE-TIME artifact. What it buys is that every image
 * and every video frame then stores as a physicality TRAJECTORY over it — the
 * same lossless, exactly-invertible serialization text already uses, where
 * ContentRoundtrip rebuilds a document's original bytes from its id alone. The
 * corpus is what scales, and the corpus gets:
 *
 *   - RUN-LENGTH, already in the format. laplace_mantissa_pack and
 *     laplace_trajectory_constituents carry run_length as a first-class field,
 *     so a flat region costs one entry, not one per pixel.
 *   - DEDUPLICATION BY CONTENT ADDRESS at every tier. Identical patches, regions
 *     and frames collapse to one id across the whole corpus; the law's video
 *     section states it outright — "unchanged regions dedup by content id" —
 *     which is most of a video.
 *
 * So the blob is amortised across every pixel the substrate will ever hold, and
 * docs/specs/33 already governs how it lands: mmap'd, postmaster-prewarmed, with
 * rule 5's declared coverage SCOPES (the t0 blob ships ASCII / BMP /
 * all-codepoints) sizing it per target.
 *
 * Blob-eligible because enumerable: channel-value (256), pixel (2^24), PCM
 * sample (65,536). The tiers above are not enumerable — a patch is a fixed ARITY
 * of pixels, not a fixed population — but they do not need to be: they are
 * trajectories over the tier below, which is exactly how word, sentence and
 * document already work.
 *
 * When the blobs land, this module becomes their lookup path and the closed form
 * becomes the generator, exactly as codepoint_table.c relates to
 * unicode_seed.cpp. The two must stay bit-identical; test_modality_atoms already
 * pins that property for super_fibonacci_point against the materialised set.
 * Blob prerequisites are spec 33's rule of engagement: a row in its table, a
 * one-way rebuild path with the reverse structurally absent, determinism +
 * staleness gates, and a stated scoping rule.
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
