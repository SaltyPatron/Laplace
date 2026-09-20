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
 *     → CURRENT LEGACY media recipe: decomposer + decimal/codepoint compose path.
 *       This is an implementation state, not the universal modality identity law.
 *       docs/invention/modality-ladder-law.md + GH #1134 own its replacement.
 *
 *   laplace_audio_root_id(pcm, n_samples, &root_id)   [modality_witness.h]
 *     → build, read collapsed root id, free tree.
 *
 * Input ABI unchanged: mono int16 PCM (packaging/decode output). Channel is a
 * partition, not a tier. The current implementation does not mint private PCM
 * atoms, but its decimal-Unicode leaf recipe is legacy and is being replaced
 * under GH #1134; do not treat it as invention authority.
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
 * CURRENT LEGACY audio ladder (GH #1134 owns correction):
 *   tier 0 Codepoint  — decimal digit chars + optional U+002D
 *   tier 1 Sample     — number composed of those codepoints
 * These tiers describe the installed recipe, not a requirement that all future
 * audio/sample identity must be textual decimal composition.
 *   tier 2 Window     — fixed hop (LAPLACE_AUDIO_WINDOW_SAMPLES)
 *   tier 3 OnsetSegment — fixed groups of windows (placeholder)
 *   tier 4 Phrase
 *   tier 5 Track
 *
 * pcm: one channel only (interleaved multi-channel is NOT supported here).
 * Packaging (media_decode → mono int16) is INPUT only. The current ladder uses
 * codepoint → Sample → …; corrected recipe/physicality semantics are tracked
 * by GH #1134 and must preserve exact source/reconstruction state.
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
