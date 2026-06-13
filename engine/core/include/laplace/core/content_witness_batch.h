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

#ifdef __cplusplus
}
#endif
