#include "laplace/core/audio_decomposer.h"

#include <stdlib.h>

#include "laplace/core/modality_decimal.h"
#include "laplace/core/tier_tree.h"

int laplace_audio_decomposer_run(
    const int16_t* pcm,
    size_t         n_samples,
    tier_tree_t**  out_tree) {
    if (!out_tree) return -1;
    *out_tree = NULL;
    if (n_samples == 0) return -4;
    if (!pcm) return -1;

    size_t n_windows = (n_samples + LAPLACE_AUDIO_WINDOW_SAMPLES - 1) / LAPLACE_AUDIO_WINDOW_SAMPLES;
    size_t n_segments = (n_windows + LAPLACE_AUDIO_SEGMENT_WINDOWS - 1) / LAPLACE_AUDIO_SEGMENT_WINDOWS;
    size_t n_phrases = (n_segments + LAPLACE_AUDIO_PHRASE_SEGMENTS - 1) / LAPLACE_AUDIO_PHRASE_SEGMENTS;

    /* Upper bound: decimal cps/sample + Sample + Window + Segment + Phrase + Track. */
    size_t cap = n_samples * (size_t)LAPLACE_DECIMAL_MAX_CPS
        + n_samples + n_windows + n_segments + n_phrases + 8;
    tier_tree_t* tree = tier_tree_new(cap);
    if (!tree) return -3;

    /*
     * Emit ALL tier-0 digit leaves first, then ALL Sample nodes, so each
     * parent’s children form a contiguous index range (tier_tree contract).
     */
    uint32_t* digit_first = (uint32_t*)malloc(n_samples * sizeof(uint32_t));
    uint32_t* digit_count = (uint32_t*)malloc(n_samples * sizeof(uint32_t));
    if (!digit_first || !digit_count) {
        free(digit_first); free(digit_count); tier_tree_free(tree); return -3;
    }

    for (size_t i = 0; i < n_samples; ++i) {
        uint32_t cps[LAPLACE_DECIMAL_MAX_CPS];
        uint32_t n_digits = laplace_decimal_codepoints_i32((int32_t)pcm[i], cps);
        digit_first[i] = (uint32_t)tier_tree_node_count(tree);
        digit_count[i] = n_digits;
        for (uint32_t d = 0; d < n_digits; ++d) {
            uint32_t idx = tier_tree_add_leaf(tree, 0, cps[d], (uint32_t)i, 1);
            if (idx == TIER_TREE_INVALID) {
                free(digit_first); free(digit_count); tier_tree_free(tree); return -3;
            }
        }
    }

    uint32_t* sample_idx = (uint32_t*)malloc(n_samples * sizeof(uint32_t));
    if (!sample_idx) {
        free(digit_first); free(digit_count); tier_tree_free(tree); return -3;
    }
    for (size_t i = 0; i < n_samples; ++i) {
        uint32_t sidx = tier_tree_add_node(
            tree, 1, digit_first[i], digit_count[i], (uint32_t)i, 1);
        if (sidx == TIER_TREE_INVALID) {
            free(digit_first); free(digit_count); free(sample_idx);
            tier_tree_free(tree); return -3;
        }
        sample_idx[i] = sidx;
    }
    free(digit_first);
    free(digit_count);

    /* Tier 2 windows — contiguous Sample nodes (fixed hop). */
    uint32_t* window_idx = (uint32_t*)malloc(n_windows * sizeof(uint32_t));
    if (!window_idx) { free(sample_idx); tier_tree_free(tree); return -3; }
    for (size_t w = 0; w < n_windows; ++w) {
        uint32_t first_sample = (uint32_t)(w * LAPLACE_AUDIO_WINDOW_SAMPLES);
        uint32_t remaining = (uint32_t)(n_samples - first_sample);
        uint32_t count = remaining < LAPLACE_AUDIO_WINDOW_SAMPLES
            ? remaining : LAPLACE_AUDIO_WINDOW_SAMPLES;
        uint32_t first = sample_idx[first_sample];
        uint32_t idx = tier_tree_add_node(tree, 2, first, count, first_sample, count);
        if (idx == TIER_TREE_INVALID) {
            free(sample_idx); free(window_idx); tier_tree_free(tree); return -3;
        }
        window_idx[w] = idx;
    }
    free(sample_idx);

    /* Tier 3 onset-segment placeholders — contiguous window nodes. */
    uint32_t* segment_idx = (uint32_t*)malloc(n_segments * sizeof(uint32_t));
    if (!segment_idx) { free(window_idx); tier_tree_free(tree); return -3; }
    for (size_t s = 0; s < n_segments; ++s) {
        uint32_t first = window_idx[s * LAPLACE_AUDIO_SEGMENT_WINDOWS];
        size_t remaining = n_windows - s * LAPLACE_AUDIO_SEGMENT_WINDOWS;
        uint32_t count = remaining < LAPLACE_AUDIO_SEGMENT_WINDOWS
            ? (uint32_t)remaining : LAPLACE_AUDIO_SEGMENT_WINDOWS;
        uint32_t idx = tier_tree_add_node(tree, 3, first, count, (uint32_t)s, count);
        if (idx == TIER_TREE_INVALID) {
            free(window_idx); free(segment_idx); tier_tree_free(tree); return -3;
        }
        segment_idx[s] = idx;
    }
    free(window_idx);

    /* Tier 4 phrases. */
    uint32_t* phrase_idx = (uint32_t*)malloc(n_phrases * sizeof(uint32_t));
    if (!phrase_idx) { free(segment_idx); tier_tree_free(tree); return -3; }
    for (size_t p = 0; p < n_phrases; ++p) {
        uint32_t first = segment_idx[p * LAPLACE_AUDIO_PHRASE_SEGMENTS];
        size_t remaining = n_segments - p * LAPLACE_AUDIO_PHRASE_SEGMENTS;
        uint32_t count = remaining < LAPLACE_AUDIO_PHRASE_SEGMENTS
            ? (uint32_t)remaining : LAPLACE_AUDIO_PHRASE_SEGMENTS;
        uint32_t idx = tier_tree_add_node(tree, 4, first, count, (uint32_t)p, count);
        if (idx == TIER_TREE_INVALID) {
            free(segment_idx); free(phrase_idx); tier_tree_free(tree); return -3;
        }
        phrase_idx[p] = idx;
    }
    free(segment_idx);

    /* Tier 5 track root. */
    uint32_t first_phrase = phrase_idx[0];
    uint32_t n_ph = (uint32_t)n_phrases;
    free(phrase_idx);
    uint32_t root = tier_tree_add_node(tree, 5, first_phrase, n_ph, 0, (uint32_t)n_samples);
    if (root == TIER_TREE_INVALID) { tier_tree_free(tree); return -3; }

    if (tier_tree_finalize(tree) != 0) { tier_tree_free(tree); return -3; }
    *out_tree = tree;
    return 0;
}
