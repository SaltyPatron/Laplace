#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Pure NFC + UAX#29 text decomposition primitive per ADR 0047.
 *
 * Input: UTF-8 bytes. Output: a populated tier_tree_t* with the tier
 * topology:
 *   tier 0 = Codepoint (leaves; atom = codepoint value; text_range_off =
 *            byte offset in the (decomposed-then-recomposed NFC form;
 *            see note below)
 *   tier 1 = Grapheme cluster (UAX#29 grapheme break)
 *   tier 2 = Word (UAX#29 word break; snapped to grapheme boundaries)
 *   tier 3 = Sentence (UAX#29 sentence break; snapped to word boundaries)
 *   tier 4 = Document root (children = all sentences)
 *
 * The input bytes are first normalized to NFC; subsequent segmentation
 * operates on the normalized codepoint stream. This means
 * text_range_off/len on the leaves refer to byte offsets within the
 * NORMALIZED form, not the original input. Same input → same NFC form →
 * same TierTree → same content-addressed hashes (RULES R7).
 *
 * Deterministic per LAPLACE_UNICODE_VERSION (compile-time constant baked
 * into the UCD tables). The tier_tree returned is finalized
 * (parent_idx populated); IDs / coords / Hilbert are NOT populated —
 * call laplace_hash_composer_run downstream.
 *
 * Returns 0 on success; non-zero on:
 *   -1: NULL args
 *   -2: invalid UTF-8 sequence
 *   -3: allocation failure */
int laplace_text_decomposer_run(
    const uint8_t* utf8,
    size_t         len,
    tier_tree_t**  out_tree);

#ifdef __cplusplus
}
#endif
