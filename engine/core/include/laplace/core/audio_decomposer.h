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
 *     → decomposer + exact integer scalar composition: PCM sample value is rendered
 *       as its canonical signed decimal codepoint sequence, composed once, and reused
 *       wherever that exact sample value occurs. Window+ = merkle/centroid.
 *
 *   laplace_audio_root_id(pcm, n_samples, &root_id)   [modality_witness.h]
 *     → build, read collapsed root id, free tree.
 *
 * Input ABI unchanged: mono int16 PCM (packaging/decode output). Channel is a
 * partition, not a tier. Identity is NOT blake3 of PCM bytes/private amplitude
 * atoms: the finite integer sample uses the shared codepoint/content law.
 * GH #1134 owns missing rate/channel/precision/occurrence/reconstruction work.
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
 * Audio ladder:
 *   tier 0 Codepoint  — canonical scalar spelling constituents
 *   tier 1 Sample     — reusable scalar/number composed from those codepoints
 * Repeating a sample value reuses this scalar content; sample occurrences carry
 * their own ordinal/time/channel/reconstruction state.
 *   tier 2 Window     — fixed hop (LAPLACE_AUDIO_WINDOW_SAMPLES)
 *   tier 3 OnsetSegment — fixed groups of windows (placeholder)
 *   tier 4 Phrase
 *   tier 5 Track
 *
 * pcm: one channel only (interleaved multi-channel is NOT supported here).
 * Packaging (media_decode → mono int16) is INPUT only. The ladder uses
 * codepoint → scalar/sample → … composition; GH #1134 tracks the additional
 * exact source/reconstruction/occurrence state around those reusable scalars.
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
