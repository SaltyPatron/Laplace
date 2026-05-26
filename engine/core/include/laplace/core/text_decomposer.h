#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Observed UTF-8 + UAX#29 text decomposition primitive per ADR 0047 (amended).
 *
 * Input: UTF-8 bytes. Output: a populated tier_tree_t* with the tier
 * topology:
 *   tier 0 = Codepoint (leaves; atom = observed scalar value)
 *   tier 1 = Grapheme cluster (UAX#29 grapheme break)
 *   tier 2 = Word (UAX#29 word break; snapped to grapheme boundaries)
 *   tier 3 = Sentence (UAX#29 sentence break; snapped to word boundaries)
 *   tier 4 = Document root (children = all sentences)
 *
 * No NFC/NFD at ingest: the codepoint stream is exactly as observed in
 * the input UTF-8. Canonical/compatibility equivalence (e.g. U+00E9 vs
 * U+0065 U+0301) is recorded as Unicode attestations, not a destructive
 * normalize step. Bit-perfect round-trip requires this. Segmentation
 * uses GB/WB/SB properties from the mmap'd perf-cache (client-side T0).
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
