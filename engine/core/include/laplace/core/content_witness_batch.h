#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/* THE content tier -> entity type id map, for EVERY lane that stages content.
 * 0 Codepoint, 1 Grapheme, 2 Word, 3 Sentence, 4+ Document -- the five names the
 * text ladder has always used. Exported because the grammar compose lane needs
 * the SAME answer: it was minting its own parallel vocabulary
 * (blake3("substrate/type/grammar/<modality>/<tree-sitter node>/v1")) and
 * stamping tree-sitter's private symbol names onto substrate entities. */
hash128_t laplace_content_tier_type_id(uint8_t tier);

int content_witness_batch_add(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id);

int content_witness_add_underscored(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id);

int content_witness_root_id_underscored(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id);

int content_witness_tree_build(
    const uint8_t* utf8,
    size_t         len,
    tier_tree_t**  out_tree);

int content_witness_emit_tree(
    intent_stage_t*    stage,
    const tier_tree_t* tree,
    const hash128_t*   source_id,
    const uint8_t*     existing_bitmap,
    size_t             bitmap_bits,
    hash128_t*         out_root_id);

void content_witness_reset(void);

int laplace_content_root_id(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id);

/* Physicality identity is (entity_id, physicality_type) ONLY. The centroid coord
 * and trajectory are DERIVED, non-exact geometry (centroids collide, e.g.
 * cat/act) and MUST NOT enter the id -- hashing the float geometry forged
 * spurious duplicate physicalities (observed: 319 chess-move rows with identical
 * coords but float-divergent trajectories). Every compose path shares this one
 * definition; do not reimplement it. */
void laplace_physicality_id_compute(
    hash128_t  entity_id,
    int16_t    physicality_type,
    hash128_t* out);

typedef void (*laplace_word_emit_fn)(void* ctx, uint32_t ordinal,
                                     const uint8_t* word_utf8, uint32_t word_len,
                                     const hash128_t* id);

int laplace_content_word_segment(
    const uint8_t*       utf8,
    size_t               len,
    laplace_word_emit_fn emit,
    void*                ctx);

#ifdef __cplusplus
}
#endif
