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

/* Build the shared content ladder in recipe-declared source representation:
 * validated original codepoints and byte offsets are retained for exact source
 * replay. Natural-language content continues to use content_witness_tree_build. */
int content_witness_source_tree_build(
    const uint8_t* utf8,
    size_t         len,
    tier_tree_t**  out_tree);

int laplace_content_source_root_id(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id);

/* Observe the exact loaded atomic Content physicality. The expected E must
 * match the floor's atom record. Copies coord/Hilbert, null trajectory, zero
 * constituents and null optional fields; emits no entity or wrapper. Returns
 * 0 on success, -1 for invalid arguments, -3 without a loaded floor, and -2 for
 * an unknown/mismatched atom or stage failure. */
int content_witness_emit_floor_atom(
    intent_stage_t* stage, uint32_t atom, const hash128_t* expected_id,
    int64_t observed_at_unix_us);

/* Emit each computed compositional physicality observation, including forms
 * whose E is already witnessed or covered by the supplied presence bitmap.
 * Those filters still suppress duplicate entity creation. The previous novel
 * placement winners precede remaining raw forms. A tier-0 natural root observes
 * its exact existing atomic floor body; interior floor leaves and collapsed
 * scaffolds produce no extra rows. No wrapper/self composition is created. The caller binds
 * the actual appended physicality span to this source and source-unit receipt. */
int content_witness_emit_tree(
    intent_stage_t*    stage,
    const tier_tree_t* tree,
    const hash128_t*   source_id,
    const uint8_t*     existing_bitmap,
    size_t             bitmap_bits,
    hash128_t*         out_root_id);

/* Root id of an ALREADY-BUILT tree — the same collapse walk emit uses. Lets a
 * caller derive once and answer "is this ladder persisted?" from the built tree
 * instead of paying laplace_content_root_id's second full derivation. */
int content_witness_tree_root_id(
    const tier_tree_t* tree,
    hash128_t*         out_root_id);

/* The same natural unit as root-id lookup and emission, including complete
 * singleton/span collapse. Copy the already composed node; do not recompute
 * its placement or select a different scaffold node for geometry inspection. */
int content_witness_tree_root_node(
    const tier_tree_t* tree,
    tier_node_view_t*  out_root);

void content_witness_reset(void);

int laplace_content_root_id(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id);

/* Legacy placement lookup address: (entity_id, physicality_type). Preserve its
 * exact layout for existing typed readers and rows. It is not an immutable
 * identity for every body that may be observed at that address. An exact body
 * can be represented as ordinary content by physicality_descriptor without
 * changing this address or the realized entity's identity. Derived geometry
 * never replaces the realized entity's canonical constituent identity. */
void laplace_physicality_id_compute(
    hash128_t  entity_id,
    int16_t    physicality_type,
    hash128_t* out);

/* Tier-floor collapse — single-child, span-identical wrappers walk to the
 * stored identity. Exported for C#/C parity (GH #904); mirrors
 * TierTree.CollapseIndex. */
uint32_t laplace_tier_tree_collapse_index(const tier_tree_t* tree, uint32_t idx);

typedef void (*laplace_word_emit_fn)(void* ctx, uint32_t ordinal,
                                     const uint8_t* word_utf8, uint32_t word_len,
                                     const hash128_t* id);

/* Emit-callback contract (#1039): word_utf8 points into the TREE's own
 * post-NFC text buffer, which is freed when this call returns. Consume the
 * bytes inside the callback (copy if needed) and NEVER compute offsets by
 * pointer arithmetic against the caller's input buffer — NFC changes byte
 * positions, so the two spaces do not correspond. */
int laplace_content_word_segment(
    const uint8_t*       utf8,
    size_t               len,
    laplace_word_emit_fn emit,
    void*                ctx);

/* Build the full content tier tree (NFC → UAX #29 ladder → merkle ids) for
 * consumers that need offsets, spans, and ids in ONE coherent space: the
 * returned tree owns its post-NFC text (tier_tree_text) and every node's
 * text_range_off/len indexes it. Caller frees with tier_tree_free. */
int laplace_content_tree_build_public(
    const uint8_t* utf8,
    size_t         len,
    tier_tree_t**  out_tree);

#ifdef __cplusplus
}
#endif
