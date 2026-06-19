#include "laplace/core/text_decomposer.h"

#include <stdlib.h>

#include "laplace/core/grapheme_floor.h"
#include "laplace/core/normalize_nfc.h"
#include "laplace/core/sentence_break.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/word_break.h"

int laplace_text_decomposer_run(const uint8_t* utf8, size_t len, tier_tree_t** out_tree) {
    if (!out_tree) return -1;
    *out_tree = NULL;
    if (!utf8 && len > 0) return -1;

    if (len == 0) {
        tier_tree_t* t = tier_tree_new(1);
        if (!t) return -3;
        uint32_t root = tier_tree_add_node(t, 4, TIER_TREE_INVALID, 0, 0, 0);
        if (root == TIER_TREE_INVALID) { tier_tree_free(t); return -3; }
        tier_tree_finalize(t);
        *out_tree = t;
        return 0;
    }

    // Canonicalization chokepoint: NFC-normalize once here so every content decomposition — and
    // therefore every content entity id, across both the native compose path and the C# callers
    // that invoke this directly — shares one canonical form ("café" NFC == "cafe´" NFD). This is
    // what makes cross-source convergence ("same content = same hash") structural. Pure-ASCII is
    // already NFC, so fast-path it to avoid an allocation on the hot path.
    const uint8_t* work = utf8;
    size_t work_len = len;
    uint8_t* nfc = NULL;
    {
        int has_non_ascii = 0;
        for (size_t i = 0; i < len; ++i) { if (utf8[i] >= 0x80) { has_non_ascii = 1; break; } }
        if (has_non_ascii) {
            size_t nfc_len = 0;
            if (laplace_normalize_nfc_utf8(utf8, len, &nfc, &nfc_len) == 0 && nfc && nfc_len > 0) {
                work = nfc;
                work_len = nfc_len;
            }
        }
    }

    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    int rc = laplace_grapheme_floor_build(work, work_len, &tree, &floor);
    free(nfc);   // the floor copies cps/offsets; the input bytes are not referenced past here
    if (rc != 0) return rc;

    uint32_t* cps = floor.cps;
    size_t    cp_n = floor.cp_n;

    size_t word_first_idx_in_tree;
    size_t word_count = 0;
    {
        word_first_idx_in_tree = tier_tree_node_count(tree);
        size_t prev_boundary = 0;
        while (prev_boundary < cp_n) {
            size_t next_boundary = laplace_word_break_next(cps, cp_n, prev_boundary);
            uint32_t g_start = floor.cp_to_graph[prev_boundary];
            uint32_t g_end   = (next_boundary > 0)
                               ? floor.cp_to_graph[next_boundary - 1] + 1
                               : g_start;
            uint32_t child_count = g_end - g_start;
            uint32_t off_start = floor.leaf_text_off[prev_boundary];
            uint32_t off_end   = (next_boundary > 0)
                                 ? floor.leaf_text_off[next_boundary - 1] + floor.leaf_text_len[next_boundary - 1]
                                 : 0;
            uint32_t idx = tier_tree_add_node(tree, 2,
                                               (uint32_t)(floor.graph_first_idx + g_start),
                                               child_count,
                                               off_start, off_end - off_start);
            if (idx == TIER_TREE_INVALID) {
                laplace_grapheme_floor_free(&floor); tier_tree_free(tree); return -3;
            }
            word_count++;
            prev_boundary = next_boundary;
        }
    }

    size_t sent_first_idx_in_tree;
    size_t sent_count = 0;
    {
        uint32_t* cp_to_word = (uint32_t*)malloc(cp_n * sizeof(uint32_t));
        if (!cp_to_word) {
            laplace_grapheme_floor_free(&floor); tier_tree_free(tree); return -3;
        }
        for (size_t w = 0; w < word_count; ++w) {
            tier_node_view_t v;
            tier_tree_get_node(tree, (uint32_t)(word_first_idx_in_tree + w), &v);
            uint32_t cp_start = 0xFFFFFFFFu, cp_end = 0;
            for (uint32_t k = 0; k < v.child_count; ++k) {
                tier_node_view_t gv;
                tier_tree_get_node(tree, v.first_child_idx + k, &gv);
                if (gv.first_child_idx < cp_start) cp_start = gv.first_child_idx;
                if (gv.first_child_idx + gv.child_count > cp_end) cp_end = gv.first_child_idx + gv.child_count;
            }
            for (uint32_t i = cp_start; i < cp_end; ++i) cp_to_word[i] = (uint32_t)w;
        }

        sent_first_idx_in_tree = tier_tree_node_count(tree);
        size_t prev_boundary = 0;
        while (prev_boundary < cp_n) {
            size_t next_boundary = laplace_sentence_break_next(cps, cp_n, prev_boundary);
            uint32_t w_start = cp_to_word[prev_boundary];
            uint32_t w_end   = (next_boundary > 0)
                               ? cp_to_word[next_boundary - 1] + 1
                               : w_start;
            uint32_t child_count = w_end - w_start;
            uint32_t off_start = floor.leaf_text_off[prev_boundary];
            uint32_t off_end   = (next_boundary > 0)
                                 ? floor.leaf_text_off[next_boundary - 1] + floor.leaf_text_len[next_boundary - 1]
                                 : 0;
            uint32_t idx = tier_tree_add_node(tree, 3,
                                               (uint32_t)(word_first_idx_in_tree + w_start),
                                               child_count,
                                               off_start, off_end - off_start);
            if (idx == TIER_TREE_INVALID) {
                free(cp_to_word);
                laplace_grapheme_floor_free(&floor); tier_tree_free(tree); return -3;
            }
            sent_count++;
            prev_boundary = next_boundary;
        }
        free(cp_to_word);
    }

    {
        uint32_t off_start = 0;
        uint32_t off_end = (cp_n > 0)
                           ? floor.leaf_text_off[cp_n - 1] + floor.leaf_text_len[cp_n - 1]
                           : 0;
        uint32_t root_idx = tier_tree_add_node(tree, 4,
                                                (uint32_t)sent_first_idx_in_tree,
                                                (uint32_t)sent_count,
                                                off_start, off_end - off_start);
        if (root_idx == TIER_TREE_INVALID) {
            laplace_grapheme_floor_free(&floor); tier_tree_free(tree); return -3;
        }
    }

    tier_tree_finalize(tree);

    laplace_grapheme_floor_free(&floor);
    *out_tree = tree;
    return 0;
}
