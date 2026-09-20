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
 * Entity-type labels for the current image/audio ladder.
 * Image: Codepoint/Number/Channel/Pixel/Patch/Region/Image.
 * Audio: Codepoint/Sample/Window/OnsetSegment/Phrase/Track.
 *
 * Number/Sample scalar content is composed from canonical codepoint sequences and
 * reused across occurrences. The modality-number ROM accelerates common 0..255 roots;
 * it does not define the numeric domain. GH #1134 owns exact media occurrence,
 * rate/channel/precision/shape and reconstruction semantics.
 */
hash128_t laplace_modality_tier_type_id(laplace_modality_t modality, uint8_t tier);

/* Shared media scalar leaf resolver: atoms are canonical codepoints, not private
 * amplitude/RGBA/PCM values. */
int laplace_modality_hash_composer_resolver(
    uint32_t atom, void* user_data,
    hash128_t* out_id, double out_coord[4], hilbert128_t* out_hilbert);

/* Compose reusable scalar content plus higher image/audio structures. */
int laplace_image_tree_build(
    const uint8_t* rgba, uint32_t width, uint32_t height, tier_tree_t** out_tree);
int laplace_audio_tree_build(
    const int16_t* pcm, size_t n_samples, tier_tree_t** out_tree);

/*
 * Emit a composed modality tree into intent_stage. Codepoint leaves are not emitted
 * because they already exist in the shared floor; reusable scalar roots/higher
 * structures are staged as needed. GH #1134 owns remaining occurrence metadata.
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
