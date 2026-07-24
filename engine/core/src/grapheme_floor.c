#include "laplace/core/grapheme_floor.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/grapheme_break.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/utf8.h"


int laplace_grapheme_floor_build(const uint8_t* utf8, size_t len,
                                 tier_tree_t** out_tree,
                                 laplace_grapheme_floor_t* out) {
    if (!out_tree || !out) return -1;
    *out_tree = NULL;
    memset(out, 0, sizeof(*out));
    if (!utf8 || len == 0) return -1;

    size_t cap = len + 1;
    uint32_t* raw_cps = (uint32_t*)malloc(cap * sizeof(uint32_t));
    if (!raw_cps) return -3;
    size_t raw_n = 0;
    size_t off = 0;
    while (off < len) {
        if (raw_n >= cap) {
            cap *= 2;
            uint32_t* n = (uint32_t*)realloc(raw_cps, cap * sizeof(uint32_t));
            if (!n) { free(raw_cps); return -3; }
            raw_cps = n;
        }
        uint32_t cp; size_t consumed;
        if (laplace_utf8_decode(utf8 + off, len - off, &cp, &consumed) != 0) {
            free(raw_cps); return -2;
        }
        raw_cps[raw_n++] = cp;
        off += consumed;
    }

    uint32_t* cps = raw_cps;
    size_t cp_n = raw_n;

    tier_tree_t* tree = tier_tree_new(cp_n * 2 + 16);
    if (!tree) { free(cps); return -3; }

    uint8_t enc[4];
    uint32_t* leaf_text_off = (uint32_t*)malloc(cp_n * sizeof(uint32_t));
    uint32_t* leaf_text_len = (uint32_t*)malloc(cp_n * sizeof(uint32_t));
    if (!leaf_text_off || !leaf_text_len) {
        free(leaf_text_off); free(leaf_text_len);
        free(cps); tier_tree_free(tree); return -3;
    }
    uint32_t running = 0;
    for (size_t i = 0; i < cp_n; ++i) {
        size_t bytes = laplace_utf8_encode(cps[i], enc);
        leaf_text_off[i] = running;
        leaf_text_len[i] = (uint32_t)bytes;
        running += (uint32_t)bytes;
    }

    for (size_t i = 0; i < cp_n; ++i) {
        uint32_t idx = tier_tree_add_leaf(tree, 0, cps[i],
                                           leaf_text_off[i], leaf_text_len[i]);
        if (idx == TIER_TREE_INVALID) {
            free(leaf_text_off); free(leaf_text_len); free(cps);
            tier_tree_free(tree); return -3;
        }
    }

    size_t graph_first_idx_in_tree = tier_tree_node_count(tree);
    size_t graph_count = 0;
    {
        size_t prev_boundary = 0;
        while (prev_boundary < cp_n) {
            size_t next_boundary = laplace_grapheme_break_next(cps, cp_n, prev_boundary);
            uint32_t child_count = (uint32_t)(next_boundary - prev_boundary);
            uint32_t off_start = leaf_text_off[prev_boundary];
            uint32_t off_end   = (next_boundary > 0)
                                 ? leaf_text_off[next_boundary - 1] + leaf_text_len[next_boundary - 1]
                                 : 0;
            uint32_t idx = tier_tree_add_node(tree, 1,
                                               (uint32_t)prev_boundary, child_count,
                                               off_start, off_end - off_start);
            if (idx == TIER_TREE_INVALID) {
                free(leaf_text_off); free(leaf_text_len); free(cps);
                tier_tree_free(tree); return -3;
            }
            graph_count++;
            prev_boundary = next_boundary;
        }
    }

    uint32_t* cp_to_graph = (uint32_t*)malloc(cp_n * sizeof(uint32_t));
    if (!cp_to_graph) {
        free(leaf_text_off); free(leaf_text_len); free(cps);
        tier_tree_free(tree); return -3;
    }
    for (size_t g = 0; g < graph_count; ++g) {
        tier_node_view_t v;
        tier_tree_get_node(tree, (uint32_t)(graph_first_idx_in_tree + g), &v);
        for (uint32_t k = 0; k < v.child_count; ++k) {
            cp_to_graph[v.first_child_idx + k] = (uint32_t)g;
        }
    }

    out->cps             = cps;
    out->cp_n            = cp_n;
    out->leaf_text_off   = leaf_text_off;
    out->leaf_text_len   = leaf_text_len;
    out->graph_first_idx = graph_first_idx_in_tree;
    out->graph_count     = graph_count;
    out->cp_to_graph     = cp_to_graph;
    *out_tree = tree;
    return 0;
}

void laplace_grapheme_floor_free(laplace_grapheme_floor_t* f) {
    if (!f) return;
    free(f->cps);
    free(f->leaf_text_off);
    free(f->leaf_text_len);
    free(f->cp_to_graph);
    memset(f, 0, sizeof(*f));
}

laplace_grapheme_floor_t* laplace_grapheme_floor_build_owned(
    const uint8_t* utf8, size_t len, tier_tree_t** out_tree) {
    laplace_grapheme_floor_t* f =
        (laplace_grapheme_floor_t*)malloc(sizeof(laplace_grapheme_floor_t));
    if (!f) { if (out_tree) *out_tree = NULL; return NULL; }
    if (laplace_grapheme_floor_build(utf8, len, out_tree, f) != 0) {
        free(f);
        return NULL;
    }
    return f;
}

size_t laplace_grapheme_floor_cp_n(const laplace_grapheme_floor_t* f) {
    return f ? f->cp_n : 0;
}
size_t laplace_grapheme_floor_graph_first_idx(const laplace_grapheme_floor_t* f) {
    return f ? f->graph_first_idx : 0;
}
size_t laplace_grapheme_floor_graph_count(const laplace_grapheme_floor_t* f) {
    return f ? f->graph_count : 0;
}
const uint32_t* laplace_grapheme_floor_leaf_text_off(const laplace_grapheme_floor_t* f) {
    return f ? f->leaf_text_off : NULL;
}
const uint32_t* laplace_grapheme_floor_leaf_text_len(const laplace_grapheme_floor_t* f) {
    return f ? f->leaf_text_len : NULL;
}
const uint32_t* laplace_grapheme_floor_cp_to_graph(const laplace_grapheme_floor_t* f) {
    return f ? f->cp_to_graph : NULL;
}

void laplace_grapheme_floor_free_owned(laplace_grapheme_floor_t* f) {
    if (!f) return;
    laplace_grapheme_floor_free(f);  
    free(f);
}

static size_t lower_bound_cp(const laplace_grapheme_floor_t* f, uint32_t b) {
    size_t lo = 0, hi = f->cp_n;
    while (lo < hi) {
        size_t mid = lo + ((hi - lo) >> 1);
        if (f->leaf_text_off[mid] < b) lo = mid + 1;
        else hi = mid;
    }
    return lo;
}

int laplace_grapheme_floor_span_to_graphemes(
    const laplace_grapheme_floor_t* f,
    uint32_t start_byte, uint32_t end_byte,
    size_t* out_g_start, size_t* out_g_end) {
    if (!f || !out_g_start || !out_g_end) return -1;
    *out_g_start = 0;
    *out_g_end   = 0;
    if (end_byte <= start_byte || f->cp_n == 0) return -1;
    size_t cp_start = lower_bound_cp(f, start_byte);
    size_t cp_end   = lower_bound_cp(f, end_byte);
    if (cp_start >= f->cp_n) return -1;
    if (cp_end <= cp_start) cp_end = cp_start + 1;
    if (cp_end > f->cp_n) cp_end = f->cp_n;
    *out_g_start = f->cp_to_graph[cp_start];
    *out_g_end   = f->cp_to_graph[cp_end - 1] + 1;
    return (*out_g_end > *out_g_start) ? 0 : -1;
}
