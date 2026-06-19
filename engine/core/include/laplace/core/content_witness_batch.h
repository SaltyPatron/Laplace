#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"

#ifdef __cplusplus
extern "C" {
#endif

int content_witness_batch_add(
    intent_stage_t*  stage,
    const uint8_t*   utf8,
    size_t           len,
    const hash128_t* source_id,
    hash128_t*       out_root_id);

void content_witness_reset(void);

int content_witness_entity_proven(const hash128_t* id);

int laplace_content_root_id(
    const uint8_t* utf8,
    size_t         len,
    hash128_t*     out_root_id);

typedef void (*laplace_word_emit_fn)(void* ctx, uint32_t ordinal,
                                     const uint8_t* word_utf8, uint32_t word_len,
                                     const hash128_t* id);

int laplace_content_word_segment(
    const uint8_t*       utf8,
    size_t               len,
    laplace_word_emit_fn emit,
    void*                ctx);

#ifdef __cplusplus
}
#endif
