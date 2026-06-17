#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif







typedef struct {
    uint32_t* cps;             
    size_t    cp_n;
    uint32_t* leaf_text_off;   
    uint32_t* leaf_text_len;   
    size_t    graph_first_idx; 
    size_t    graph_count;     
    uint32_t* cp_to_graph;     
} laplace_grapheme_floor_t;






int laplace_grapheme_floor_build(const uint8_t* utf8, size_t len,
                                 tier_tree_t** out_tree,
                                 laplace_grapheme_floor_t* out);

void laplace_grapheme_floor_free(laplace_grapheme_floor_t* f);





laplace_grapheme_floor_t* laplace_grapheme_floor_build_owned(
    const uint8_t* utf8, size_t len, tier_tree_t** out_tree);

size_t          laplace_grapheme_floor_cp_n(const laplace_grapheme_floor_t* f);
size_t          laplace_grapheme_floor_graph_first_idx(const laplace_grapheme_floor_t* f);
size_t          laplace_grapheme_floor_graph_count(const laplace_grapheme_floor_t* f);
const uint32_t* laplace_grapheme_floor_leaf_text_off(const laplace_grapheme_floor_t* f);
const uint32_t* laplace_grapheme_floor_leaf_text_len(const laplace_grapheme_floor_t* f);
const uint32_t* laplace_grapheme_floor_cp_to_graph(const laplace_grapheme_floor_t* f);

void laplace_grapheme_floor_free_owned(laplace_grapheme_floor_t* f);



int laplace_grapheme_floor_span_to_graphemes(
    const laplace_grapheme_floor_t* f,
    uint32_t start_byte, uint32_t end_byte,
    size_t* out_g_start, size_t* out_g_end);

#ifdef __cplusplus
}
#endif
