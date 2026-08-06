#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/modality_atoms.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Modality entity-type floor labels (blake3 of the name), sibling of
 * laplace_content_tier_type_id. Tier 0 is always "Codepoint" (shared T0 floor).
 * Image: 0 Codepoint, 1 Number, 2 Channel, 3 Pixel, 4 Patch, 5 Region, 6 Image.
 * Audio: 0 Codepoint, 1 Sample, 2 Window, 3 OnsetSegment, 4 Phrase, 5 Track.
 * Leaf atoms are Unicode codepoints; compose uses codepoint_table_resolve_atom.
 * Sample/Number ids: modality_number_perfcache O(1) for 0..255 when loaded,
 * else laplace_content_root_id of the decimal digit UTF-8 (ScalarId law).
 * Not merkle of child ids; not packed-RGBA/PCM blake3.
 */
hash128_t laplace_modality_tier_type_id(laplace_modality_t modality, uint8_t tier);

/* hash_composer atom resolver — atom is a Unicode codepoint (user_data unused). */
int laplace_modality_hash_composer_resolver(
    uint32_t atom, void* user_data,
    hash128_t* out_id, double out_coord[4], hilbert128_t* out_hilbert);

/* Compose: decomposer tree + codepoint/number compose paths. */
int laplace_image_tree_build(
    const uint8_t* rgba, uint32_t width, uint32_t height, tier_tree_t** out_tree);
int laplace_audio_tree_build(
    const int16_t* pcm, size_t n_samples, tier_tree_t** out_tree);

/*
 * Emit a composed modality tree into intent_stage.
 * Tier-0 Codepoint leaves are NOT emitted (shared T0 perfcache). Higher tiers emit.
 */
int laplace_modality_witness_emit_tree(
    intent_stage_t*       stage,
    const tier_tree_t*    tree,
    laplace_modality_t    modality,
    const hash128_t*      source_id,
    const uint8_t*        existing_bitmap,
    size_t                bitmap_bits,
    hash128_t*            out_root_id);

/* Cheap root id without staging (compose + read collapsed root). */
int laplace_image_root_id(
    const uint8_t* rgba, uint32_t width, uint32_t height, hash128_t* out_root_id);
int laplace_audio_root_id(
    const int16_t* pcm, size_t n_samples, hash128_t* out_root_id);

#ifdef __cplusplus
}
#endif
