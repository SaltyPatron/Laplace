#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Decompose UTF-8 content and append entity/physicality rows to intent_stage.
 * Returns 0 on success; -1 invalid args; -2 decompose/compose failure. */
int content_witness_batch_add(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id);

/* The id of the natural content unit of UTF-8 input — the SAME tree builder,
 * composer, and collapse law as content_witness_batch_add, minus the staging.
 * For a single token this is the wordform id; for a single grapheme the
 * grapheme/codepoint id (collapse law); for longer text the composed unit.
 * This is THE lookup law: anything deposited by the content path is found by
 * this and nothing else. Requires the perfcache (codepoint floor) loaded.
 * Returns 0 on success; -1 invalid args; -2 empty/invalid UTF-8 or compose
 * failure; -3 perfcache not loaded. */
int laplace_content_root_id(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id);

/* Per-word emit callback for laplace_content_word_segment. word_utf8 points
 * INTO the caller's input buffer (valid only during the call); id is the
 * content id matching how that word was deposited (collapse law applied). */
typedef void (*laplace_word_emit_fn)(void* ctx, uint32_t ordinal,
                                     const uint8_t* word_utf8, uint32_t word_len,
                                     const hash128_t* id);

/* THE omniglottal word segmentation: decompose UTF-8 with the engine's UAX#29
 * word law (the same decomposer word_id/deposition use), then emit each
 * content word in order — ordinal, surface span, and deposition-matching id.
 * Whitespace-run "words" (WSegSpace) are separators and are NOT emitted. No
 * regex, no ASCII character class: 中文 segments by the same law as English.
 * Returns 0 on success; -1 bad args; -2 empty/invalid UTF-8; -3 perfcache
 * not loaded. */
int laplace_content_word_segment(
    const uint8_t*       utf8,
    size_t               len,
    laplace_word_emit_fn emit,
    void*                ctx);

#ifdef __cplusplus
}
#endif
