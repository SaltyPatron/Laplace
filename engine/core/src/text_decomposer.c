#include "laplace/core/text_decomposer.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/grapheme_break.h"
#include "laplace/core/normalize_nfc.h"
#include "laplace/core/sentence_break.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/word_break.h"

/* UTF-8 decoder — strict; returns 0 on success and writes one codepoint
 * to *out_cp + the byte length consumed to *out_consumed. Returns -1 on
 * malformed input. */
static int utf8_decode(const uint8_t* p, size_t remaining,
                       uint32_t* out_cp, size_t* out_consumed) {
    if (remaining == 0) return -1;
    uint8_t b0 = p[0];
    if (b0 < 0x80) { *out_cp = b0; *out_consumed = 1; return 0; }
    if ((b0 & 0xE0) == 0xC0) {
        if (remaining < 2) return -1;
        uint8_t b1 = p[1];
        if ((b1 & 0xC0) != 0x80) return -1;
        uint32_t cp = ((uint32_t)(b0 & 0x1F) << 6) | (b1 & 0x3F);
        if (cp < 0x80) return -1;  /* overlong */
        *out_cp = cp; *out_consumed = 2; return 0;
    }
    if ((b0 & 0xF0) == 0xE0) {
        if (remaining < 3) return -1;
        uint8_t b1 = p[1], b2 = p[2];
        if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80) return -1;
        uint32_t cp = ((uint32_t)(b0 & 0x0F) << 12)
                    | ((uint32_t)(b1 & 0x3F) << 6)
                    | (b2 & 0x3F);
        if (cp < 0x800) return -1;
        if (cp >= 0xD800 && cp <= 0xDFFF) return -1;  /* surrogate */
        *out_cp = cp; *out_consumed = 3; return 0;
    }
    if ((b0 & 0xF8) == 0xF0) {
        if (remaining < 4) return -1;
        uint8_t b1 = p[1], b2 = p[2], b3 = p[3];
        if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80) return -1;
        uint32_t cp = ((uint32_t)(b0 & 0x07) << 18)
                    | ((uint32_t)(b1 & 0x3F) << 12)
                    | ((uint32_t)(b2 & 0x3F) << 6)
                    | (b3 & 0x3F);
        if (cp < 0x10000 || cp > 0x10FFFF) return -1;
        *out_cp = cp; *out_consumed = 4; return 0;
    }
    return -1;
}

/* UTF-8 encoder — writes 1..4 bytes for a codepoint, returns byte length. */
static size_t utf8_encode(uint32_t cp, uint8_t out[4]) {
    if (cp < 0x80) { out[0] = (uint8_t)cp; return 1; }
    if (cp < 0x800) {
        out[0] = 0xC0 | (uint8_t)(cp >> 6);
        out[1] = 0x80 | (uint8_t)(cp & 0x3F);
        return 2;
    }
    if (cp < 0x10000) {
        out[0] = 0xE0 | (uint8_t)(cp >> 12);
        out[1] = 0x80 | (uint8_t)((cp >> 6) & 0x3F);
        out[2] = 0x80 | (uint8_t)(cp & 0x3F);
        return 3;
    }
    out[0] = 0xF0 | (uint8_t)(cp >> 18);
    out[1] = 0x80 | (uint8_t)((cp >> 12) & 0x3F);
    out[2] = 0x80 | (uint8_t)((cp >> 6) & 0x3F);
    out[3] = 0x80 | (uint8_t)(cp & 0x3F);
    return 4;
}

int laplace_text_decomposer_run(const uint8_t* utf8, size_t len, tier_tree_t** out_tree) {
    if (!out_tree) return -1;
    *out_tree = NULL;
    if (!utf8 && len > 0) return -1;

    /* Empty input → empty (but valid) tree with just a root document
     * node. Callers can detect via tier_tree_node_count == 1. */
    if (len == 0) {
        tier_tree_t* t = tier_tree_new(1);
        if (!t) return -3;
        uint32_t root = tier_tree_add_node(t, /*tier=*/4, TIER_TREE_INVALID, 0, 0, 0);
        if (root == TIER_TREE_INVALID) { tier_tree_free(t); return -3; }
        tier_tree_finalize(t);
        *out_tree = t;
        return 0;
    }

    /* === Stage 1: UTF-8 decode === */
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
        if (utf8_decode(utf8 + off, len - off, &cp, &consumed) != 0) {
            free(raw_cps); return -2;
        }
        raw_cps[raw_n++] = cp;
        off += consumed;
    }

    /* === Stage 2: NFC normalize === */
    size_t need = laplace_normalize_nfc(raw_cps, raw_n, NULL, 0);
    uint32_t* nfc_cps = (uint32_t*)malloc((need + 1) * sizeof(uint32_t));
    if (!nfc_cps) { free(raw_cps); return -3; }
    size_t nfc_n = laplace_normalize_nfc(raw_cps, raw_n, nfc_cps, need);
    free(raw_cps);

    /* === Stage 3: Build the tier tree === */
    tier_tree_t* tree = tier_tree_new(nfc_n * 2 + 16);
    if (!tree) { free(nfc_cps); return -3; }

    /* Compute byte offset of each codepoint in the NFC form (so leaf
     * text_range_off refers into the NORMALIZED utf8 byte stream, not
     * the original). We don't actually store the NFC byte buffer; we
     * just need offsets for downstream consumers that might want them. */
    uint8_t enc[4];
    uint32_t* leaf_text_off = (uint32_t*)malloc(nfc_n * sizeof(uint32_t));
    uint32_t* leaf_text_len = (uint32_t*)malloc(nfc_n * sizeof(uint32_t));
    if (!leaf_text_off || !leaf_text_len) {
        free(leaf_text_off); free(leaf_text_len);
        free(nfc_cps); tier_tree_free(tree); return -3;
    }
    uint32_t running = 0;
    for (size_t i = 0; i < nfc_n; ++i) {
        size_t bytes = utf8_encode(nfc_cps[i], enc);
        leaf_text_off[i] = running;
        leaf_text_len[i] = (uint32_t)bytes;
        running += (uint32_t)bytes;
    }

    /* Tier 0 — codepoint leaves */
    for (size_t i = 0; i < nfc_n; ++i) {
        uint32_t idx = tier_tree_add_leaf(tree, /*tier=*/0, nfc_cps[i],
                                           leaf_text_off[i], leaf_text_len[i]);
        if (idx == TIER_TREE_INVALID) {
            free(leaf_text_off); free(leaf_text_len); free(nfc_cps);
            tier_tree_free(tree); return -3;
        }
    }

    /* Tier 1 — graphemes (codepoint children). Walk grapheme boundaries. */
    size_t graph_first_idx_in_tree;
    size_t graph_count = 0;
    {
        graph_first_idx_in_tree = tier_tree_node_count(tree);
        size_t prev_boundary = 0;
        while (prev_boundary < nfc_n) {
            size_t next_boundary = laplace_grapheme_break_next(nfc_cps, nfc_n, prev_boundary);
            uint32_t child_count = (uint32_t)(next_boundary - prev_boundary);
            uint32_t off_start = leaf_text_off[prev_boundary];
            uint32_t off_end   = (next_boundary > 0)
                                 ? leaf_text_off[next_boundary - 1] + leaf_text_len[next_boundary - 1]
                                 : 0;
            uint32_t idx = tier_tree_add_node(tree, /*tier=*/1,
                                               (uint32_t)prev_boundary, child_count,
                                               off_start, off_end - off_start);
            if (idx == TIER_TREE_INVALID) {
                free(leaf_text_off); free(leaf_text_len); free(nfc_cps);
                tier_tree_free(tree); return -3;
            }
            graph_count++;
            prev_boundary = next_boundary;
        }
    }

    /* Tier 2 — words (grapheme children). Snap word boundaries to
     * grapheme boundaries. We pre-compute a codepoint-index → grapheme-
     * index lookup from the just-emitted tier-1 nodes. */
    size_t word_first_idx_in_tree;
    size_t word_count = 0;
    {
        /* Build codepoint→grapheme-index map. Grapheme indices into the
         * TIER-1 node positions (offset by graph_first_idx_in_tree). */
        uint32_t* cp_to_graph = (uint32_t*)malloc(nfc_n * sizeof(uint32_t));
        if (!cp_to_graph) {
            free(leaf_text_off); free(leaf_text_len); free(nfc_cps);
            tier_tree_free(tree); return -3;
        }
        for (size_t g = 0; g < graph_count; ++g) {
            tier_node_view_t v;
            tier_tree_get_node(tree, (uint32_t)(graph_first_idx_in_tree + g), &v);
            for (uint32_t k = 0; k < v.child_count; ++k) {
                cp_to_graph[v.first_child_idx + k] = (uint32_t)g;
            }
        }

        word_first_idx_in_tree = tier_tree_node_count(tree);
        size_t prev_boundary = 0;
        while (prev_boundary < nfc_n) {
            size_t next_boundary = laplace_word_break_next(nfc_cps, nfc_n, prev_boundary);
            /* Snap [prev_boundary, next_boundary) to grapheme indices. */
            uint32_t g_start = cp_to_graph[prev_boundary];
            uint32_t g_end   = (next_boundary > 0)
                               ? cp_to_graph[next_boundary - 1] + 1
                               : g_start;
            uint32_t child_count = g_end - g_start;
            uint32_t off_start = leaf_text_off[prev_boundary];
            uint32_t off_end   = (next_boundary > 0)
                                 ? leaf_text_off[next_boundary - 1] + leaf_text_len[next_boundary - 1]
                                 : 0;
            uint32_t idx = tier_tree_add_node(tree, /*tier=*/2,
                                               (uint32_t)(graph_first_idx_in_tree + g_start),
                                               child_count,
                                               off_start, off_end - off_start);
            if (idx == TIER_TREE_INVALID) {
                free(cp_to_graph); free(leaf_text_off); free(leaf_text_len);
                free(nfc_cps); tier_tree_free(tree); return -3;
            }
            word_count++;
            prev_boundary = next_boundary;
        }
        free(cp_to_graph);
    }

    /* Tier 3 — sentences (word children). Snap sentence boundaries to
     * word boundaries via a codepoint→word-index map. */
    size_t sent_first_idx_in_tree;
    size_t sent_count = 0;
    {
        uint32_t* cp_to_word = (uint32_t*)malloc(nfc_n * sizeof(uint32_t));
        if (!cp_to_word) {
            free(leaf_text_off); free(leaf_text_len); free(nfc_cps);
            tier_tree_free(tree); return -3;
        }
        for (size_t w = 0; w < word_count; ++w) {
            tier_node_view_t v;
            tier_tree_get_node(tree, (uint32_t)(word_first_idx_in_tree + w), &v);
            /* word children are graphemes; need to map back to codepoints
             * via the grapheme nodes */
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
        while (prev_boundary < nfc_n) {
            size_t next_boundary = laplace_sentence_break_next(nfc_cps, nfc_n, prev_boundary);
            uint32_t w_start = cp_to_word[prev_boundary];
            uint32_t w_end   = (next_boundary > 0)
                               ? cp_to_word[next_boundary - 1] + 1
                               : w_start;
            uint32_t child_count = w_end - w_start;
            uint32_t off_start = leaf_text_off[prev_boundary];
            uint32_t off_end   = (next_boundary > 0)
                                 ? leaf_text_off[next_boundary - 1] + leaf_text_len[next_boundary - 1]
                                 : 0;
            uint32_t idx = tier_tree_add_node(tree, /*tier=*/3,
                                               (uint32_t)(word_first_idx_in_tree + w_start),
                                               child_count,
                                               off_start, off_end - off_start);
            if (idx == TIER_TREE_INVALID) {
                free(cp_to_word); free(leaf_text_off); free(leaf_text_len);
                free(nfc_cps); tier_tree_free(tree); return -3;
            }
            sent_count++;
            prev_boundary = next_boundary;
        }
        free(cp_to_word);
    }

    /* Tier 4 — document root: all sentences. */
    {
        uint32_t off_start = 0;
        uint32_t off_end = (nfc_n > 0)
                           ? leaf_text_off[nfc_n - 1] + leaf_text_len[nfc_n - 1]
                           : 0;
        uint32_t root_idx = tier_tree_add_node(tree, /*tier=*/4,
                                                (uint32_t)sent_first_idx_in_tree,
                                                (uint32_t)sent_count,
                                                off_start, off_end - off_start);
        if (root_idx == TIER_TREE_INVALID) {
            free(leaf_text_off); free(leaf_text_len); free(nfc_cps);
            tier_tree_free(tree); return -3;
        }
    }

    tier_tree_finalize(tree);

    free(leaf_text_off);
    free(leaf_text_len);
    free(nfc_cps);
    *out_tree = tree;
    return 0;
}
