#include "laplace/core/text_decomposer.h"

#include <stdlib.h>

#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/grapheme_floor.h"
#include "laplace/core/normalize_nfc.h"
#include "laplace/core/sentence_break.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/utf8.h"
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

    const uint8_t* work = utf8;
    size_t work_len = len;
    uint8_t* owned = NULL;  /* the buffer the tree will own — NFC output or an input copy */
    {
        int has_non_ascii = 0;
        for (size_t i = 0; i < len; ++i) { if (utf8[i] >= 0x80) { has_non_ascii = 1; break; } }
        if (has_non_ascii) {
            /* HARD error on NFC failure (#1039): the old fallthrough built the
             * tree over raw bytes, so the same content minted different ids
             * depending on a transient failure — silent identity corruption.
             * Malformed input keeps its own contract: validate first so bad
             * UTF-8 stays -2 (the grapheme floor's code) and -6 is reserved
             * for the normalizer failing on VALID input. */
            {
                size_t i2 = 0;
                while (i2 < len) {
                    uint32_t cp; size_t consumed;
                    if (laplace_utf8_decode(utf8 + i2, len - i2, &cp, &consumed) != 0)
                        return -2;
                    i2 += consumed;
                }
            }
            size_t nfc_len = 0;
            if (laplace_normalize_nfc_utf8(utf8, len, &owned, &nfc_len) != 0
                || !owned || nfc_len == 0) {
                free(owned);
                return -6;
            }
            work = owned;
            work_len = nfc_len;
        } else {
            /* ASCII input: copy so the tree still owns the exact bytes its
             * offsets index — consumers slice tier_tree_text, never the
             * caller's buffer (the caller's may be freed or, post-NFC in the
             * other arm, laid out differently). */
            owned = (uint8_t*)malloc(len > 0 ? len : 1);
            if (!owned) return -3;
            memcpy(owned, utf8, len);
        }
    }

    tier_tree_t* tree = NULL;
    laplace_grapheme_floor_t floor;
    int rc = laplace_grapheme_floor_build(work, work_len, &tree, &floor);
    if (rc != 0) { free(owned); return rc; }
    if (tier_tree_set_text(tree, owned, work_len) != 0) {
        free(owned);
        laplace_grapheme_floor_free(&floor); tier_tree_free(tree);
        return -3;
    }
    /* owned now belongs to the tree; freed by tier_tree_free on every path. */

    uint32_t* cps = floor.cps;
    size_t    cp_n = floor.cp_n;

    size_t word_first_idx_in_tree;
    size_t word_count = 0;
    {
        word_first_idx_in_tree = tier_tree_node_count(tree);
        size_t prev_boundary = 0;
        while (prev_boundary < cp_n) {
            size_t next_boundary = laplace_word_break_next(cps, cp_n, prev_boundary);
            /* SNAP OUTWARD to a grapheme boundary (#1040): UAX #29 does not
             * guarantee word boundaries are a superset of grapheme boundaries
             * (measured: 3,730 codepoint triples — U+0E33/U+0EB3 SpacingMark,
             * the 27 Prepend codepoints). A boundary inside a cluster gave two
             * word nodes the same single grapheme child — one id for two
             * surfaces, and an orphaned node. Extending to the cluster's end
             * keeps grapheme ⊂ word an invariant, not an assumption. */
            while (next_boundary > 0 && next_boundary < cp_n
                   && floor.cp_to_graph[next_boundary] == floor.cp_to_graph[next_boundary - 1]) {
                next_boundary++;
            }
            uint32_t g_start = floor.cp_to_graph[prev_boundary];
            uint32_t g_end   = (next_boundary > 0)
                               ? floor.cp_to_graph[next_boundary - 1] + 1
                               : g_start;
            /* Whitespace-only runs split PER GRAPHEME (#1042): a multi-space
             * or CRLF run otherwise composed a tier-2 Word entity — words no
             * human wrote, placed at norm 1.0 in the arena walks and sense
             * elections read. Split, each single-grapheme slot collapses to
             * its atom/cluster (the existing single-child law), so the run
             * survives byte-exactly on the parent trajectory while minting no
             * word-tier entity. */
            int all_ws = 1;
            for (size_t k = prev_boundary; k < next_boundary; ++k) {
                if (!laplace_codepoint_is_whitespace(cps[k])) { all_ws = 0; break; }
            }
            if (all_ws && g_end - g_start > 1) {
                for (uint32_t g = g_start; g < g_end; ++g) {
                    tier_node_view_t gv;
                    tier_tree_get_node(tree, (uint32_t)(floor.graph_first_idx + g), &gv);
                    uint32_t idx = tier_tree_add_node(tree, 2,
                                                       (uint32_t)(floor.graph_first_idx + g),
                                                       1,
                                                       gv.text_range_off, gv.text_range_len);
                    if (idx == TIER_TREE_INVALID) {
                        laplace_grapheme_floor_free(&floor); tier_tree_free(tree); return -3;
                    }
                    word_count++;
                }
                prev_boundary = next_boundary;
                continue;
            }
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
            /* SNAP OUTWARD to a word boundary (#1040): SB11 breaks after an
             * ATerm that WB6/WB7 keep inside one word (cased letter + '.' +
             * uncased script — 56 measured triples, Hebrew/Devanagari/
             * Malayalam/Hangul). Unsnapped, two sentence nodes shared the
             * containing word's id and the emit dedup silently dropped them —
             * sentences vanished from the substrate for such text. */
            while (next_boundary > 0 && next_boundary < cp_n
                   && cp_to_word[next_boundary] == cp_to_word[next_boundary - 1]) {
                next_boundary++;
            }
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
