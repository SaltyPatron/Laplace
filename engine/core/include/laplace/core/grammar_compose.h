#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/grammar_decomposer.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    hash128_t id;
    uint8_t   tier;
    uint8_t   _pad[3];
    hash128_t type_id;
} laplace_compose_entity_t;

typedef struct {
    hash128_t    id;
    hash128_t    entity_id;
    double       coord[4];
    hilbert128_t hilbert;
    double*      trajectory_xyzm;
    size_t       trajectory_n;
    size_t       n_constituents;
} laplace_compose_physicality_t;

typedef struct {
    hash128_t subject_id;
    hash128_t object_id;
    int64_t   games;
} laplace_compose_precedes_t;

typedef struct {
    uint32_t  start_byte;
    uint32_t  end_byte;
    hash128_t entity_id;
} laplace_compose_span_t;

typedef struct {
    laplace_compose_entity_t*        entities;
    size_t                           entity_count;
    laplace_compose_physicality_t*   physicalities;
    size_t                           phys_count;
    laplace_compose_precedes_t*      precedes;
    size_t                           precedes_count;
    laplace_compose_span_t*          spans;
    size_t                           span_count;
    hash128_t                        root_id;
    /* Containment tier tree over the emitted entities: node i corresponds 1:1 to entities[i]
     * (same id, same tier), node->parent points at the entity index of its compositional parent
     * (TIER_TREE_INVALID for roots, graphemes and type-meta nodes). Built by laplace_grammar_compose;
     * owned by this result and freed by laplace_compose_result_free. Consumed managed-side by
     * MerkleDedup.TrunkShortcircuit to emit only novel subtrees. May be NULL if the tree could not
     * be allocated (callers must fall back to emitting all entities). */
    tier_tree_t*                     tree;
} laplace_compose_result_t;




int laplace_grammar_compose(
    const uint8_t*              utf8,
    size_t                      len,
    laplace_ast_t*              ast,
    const char*                 modality_id,
    hash128_t                   source_id,
    hash128_t                   type_meta_id,
    laplace_compose_result_t**  out);

/* Light compose for probe-before-materialize: entities + tier tree + spans + PRECEDES,
 * no physicality arrays (no trajectory_build). Pair with laplace_grammar_compose_materialize_phys
 * before drain when novel subtrees remain. */
int laplace_grammar_compose_probe(
    const uint8_t*              utf8,
    size_t                      len,
    laplace_ast_t*              ast,
    const char*                 modality_id,
    hash128_t                   source_id,
    hash128_t                   type_meta_id,
    laplace_compose_result_t**  out);

/* Populate physicalities on a probe result (no-op if already materialized). */
int laplace_grammar_compose_materialize_phys(
    laplace_compose_result_t*   r,
    const uint8_t*              utf8,
    size_t                      len,
    laplace_ast_t*              ast,
    const char*                 modality_id);


int laplace_grammar_compose_node_id(
    const uint8_t*     utf8,
    size_t             len,
    laplace_ast_t*     ast,
    const char*        modality_id,
    size_t             ast_node_index,
    hash128_t*         out_id,
    uint8_t*           out_tier);

/* Cheap root-trunk id for compose-before-probe: same as node_id index 0, no full materialize. */
int laplace_grammar_compose_row_root(
    const uint8_t*     utf8,
    size_t             len,
    laplace_ast_t*     ast,
    const char*        modality_id,
    hash128_t*         out_id,
    uint8_t*           out_tier);


int laplace_compose_span_lookup(
    const laplace_compose_result_t* r,
    uint32_t start_byte,
    uint32_t end_byte,
    hash128_t* out_id);

void laplace_compose_result_free(laplace_compose_result_t* r);

size_t laplace_compose_entity_count(const laplace_compose_result_t* r);
size_t laplace_compose_physicality_count(const laplace_compose_result_t* r);
size_t laplace_compose_precedes_count(const laplace_compose_result_t* r);
hash128_t laplace_compose_root_id(const laplace_compose_result_t* r);

/* Borrowed pointer to the containment tier tree (see laplace_compose_result_t::tree). The returned
 * tree is owned by the compose result and must NOT be freed by the caller; it is valid until
 * laplace_compose_result_free is called. Returns NULL if no tree was built. */
tier_tree_t* laplace_compose_get_tier_tree(const laplace_compose_result_t* r);

int laplace_compose_get_entity(const laplace_compose_result_t* r, size_t i,
                               laplace_compose_entity_t* out);
int laplace_compose_get_physicality(const laplace_compose_result_t* r, size_t i,
                                    laplace_compose_physicality_t* out);
int laplace_compose_get_precedes(const laplace_compose_result_t* r, size_t i,
                                 laplace_compose_precedes_t* out);

/*
 * Drain compose entities + physicalities + PRECEDES aggregated attestations into `stage`.
 * When `existing_bitmap` is non-null with `bitmap_bits` >= tier-tree node count, only novel
 * subtrees are emitted (merkle_dedup_trunk_shortcircuit). `witness_weight` scales PRECEDES Glicko.
 * Returns 0 on success.
 */
int laplace_compose_drain_into_stage(
    const laplace_compose_result_t* r,
    intent_stage_t*                 stage,
    const hash128_t*                source_id,
    int64_t                         now_unix_us,
    double                          witness_weight,
    const uint8_t*                  existing_bitmap,
    size_t                          bitmap_bits);

#ifdef __cplusplus
}
#endif
