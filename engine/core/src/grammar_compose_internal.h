#ifndef LAPLACE_CORE_GRAMMAR_COMPOSE_INTERNAL_H
#define LAPLACE_CORE_GRAMMAR_COMPOSE_INTERNAL_H

#include "laplace/core/grammar_compose.h"
#include "laplace/core/grapheme_floor.h"
#include "laplace/core/tier_tree.h"

typedef struct {
    hash128_t* comp_id;
    double*    comp_coord;
    uint8_t*   comp_tier;
    uint8_t*   entity_tier;
    uint8_t*   comp_valid;
    size_t     n;
} compose_state_t;

int laplace_compose_codepoint_resolver(uint32_t atom, void* ctx,
                                       hash128_t* out_id, double out_coord[4],
                                       hilbert128_t* out_hb);
int laplace_is_json_modality(const char* modality_id);
int laplace_json_node_is_string(laplace_ast_t* ast, const laplace_ast_node_t* node);
int laplace_json_node_is_string_fragment(laplace_ast_t* ast, const laplace_ast_node_t* node);

int laplace_compose_push_entity(laplace_compose_result_t* r, hash128_t id, uint8_t tier,
                                hash128_t type_id, uint8_t packaging);
int laplace_compose_id_push(hash128_t** ids, size_t* count, size_t* cap, hash128_t id);
int laplace_compose_push_phys(laplace_compose_result_t* r, hash128_t entity_id,
                              double coord[4], hilbert128_t* hb,
                              const hash128_t* child_ids, const uint64_t* child_flags, size_t m);

int laplace_compose_ast_nodes(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                              const char* modality_id,
                              const laplace_grapheme_floor_t* floor, tier_tree_t* tree,
                              laplace_compose_result_t* r,
                              hash128_t** emitted_entity, size_t* emitted_entity_n,
                              size_t* emitted_entity_cap,
                              compose_state_t* st);

#endif
