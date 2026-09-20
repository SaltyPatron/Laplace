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
 * Entity-type labels for the CURRENT legacy image/audio ladder.
 * Image currently labels Codepoint/Number/Channel/Pixel/Patch/Region/Image;
 * audio labels Codepoint/Sample/Window/OnsetSegment/Phrase/Track.
 *
 * The decimal/codepoint leaf representation and modality-number ROM are
 * implementation state, not the universal modality identity law. The corrected
 * provider/recipe/physicality contract is docs/invention/modality-ladder-law.md
 * and GH #1134. Do not preserve these labels/tiers merely to protect this ABI.
 */
hash128_t laplace_modality_tier_type_id(laplace_modality_t modality, uint8_t tier);

/* Legacy media-recipe resolver: current atoms are Unicode codepoints.
 * GH #1134 owns replacement; this signature is not invention authority. */
int laplace_modality_hash_composer_resolver(
    uint32_t atom, void* user_data,
    hash128_t* out_id, double out_coord[4], hilbert128_t* out_hilbert);

/* Compose under the currently installed legacy codepoint/number media recipe. */
int laplace_image_tree_build(
    const uint8_t* rgba, uint32_t width, uint32_t height, tier_tree_t** out_tree);
int laplace_audio_tree_build(
    const int16_t* pcm, size_t n_samples, tier_tree_t** out_tree);

/*
 * Emit a composed modality tree into intent_stage under the current recipe.
 * Current Codepoint leaves are not emitted because they reuse the Unicode perfcache.
 * The corrected recipe may change this shape; see GH #1134.
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
