#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * C API surface for C# P/Invoke (AudioTierSpine / NativeInterop):
 *
 *   laplace_audio_decomposer_run(pcm, n_samples, &tree)
 *     → tier_tree only (no hash compose). Caller frees with tier_tree_free.
 *
 *   laplace_audio_tree_build(pcm, n_samples, &tree)   [modality_witness.h]
 *     → decomposer + compose: T0 = codepoint_table_resolve_atom;
 *       Sample = laplace_content_root_id(decimal UTF-8) (ScalarId / Number law);
 *       Window+ = merkle/centroid.
 *
 *   laplace_audio_root_id(pcm, n_samples, &root_id)   [modality_witness.h]
 *     → build, read collapsed root id, free tree.
 *
 * Input ABI unchanged: mono int16 PCM (packaging/decode output). Channel is a
 * partition, not a tier. Identity is NOT blake3 of PCM bytes / private PCM
 * atoms — leaves are Unicode digit (and optional U+002D) codepoints.
 *
 * Tier labels (laplace_modality_tier_type_id AUDIO):
 *   0 Codepoint, 1 Sample, 2 Window, 3 OnsetSegment, 4 Phrase, 5 Track
 * (Was: 0 Sample, 1 Frame, 2 OnsetSegment, 3 Phrase, 4 Track — Frame→Window;
 * Codepoint floor inserted; Sample is the Number tier via modality_decimal.)
 *
 * MaxAudioTiers / existence-round counts in C# must cover tiers 0..5.
 */

/* Fixed witnessed-infra hop sizes. Real onset detection is calculated-layer later. */
#define LAPLACE_AUDIO_WINDOW_SAMPLES   512u
#define LAPLACE_AUDIO_SEGMENT_WINDOWS  4u
#define LAPLACE_AUDIO_PHRASE_SEGMENTS  8u

/*
 * Audio ladder (witnessed infra) — UAX#29-analog above the shared codepoint floor:
 *   tier 0 Codepoint  — decimal digit chars + optional U+002D (laplace_decimal_codepoints_i32)
 *   tier 1 Sample     — number composed of those codepoints (same law as image channels)
 *   tier 2 Window     — fixed hop (LAPLACE_AUDIO_WINDOW_SAMPLES)
 *   tier 3 OnsetSegment — fixed groups of windows (placeholder)
 *   tier 4 Phrase
 *   tier 5 Track
 *
 * pcm: one channel only (interleaved multi-channel is NOT supported here).
 * Packaging (media_decode → mono int16) is INPUT only; ladder identity is the
 * codepoint → Sample → … composition tree (no forged PCM tier-0).
 *
 * Returns 0 on success; negative on error (-1 bad args, -3 OOM/tree, -4 empty).
 */
int laplace_audio_decomposer_run(
    const int16_t* pcm,
    size_t         n_samples,
    tier_tree_t**  out_tree);

#ifdef __cplusplus
}
#endif
