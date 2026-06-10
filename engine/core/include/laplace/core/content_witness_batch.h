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

#ifdef __cplusplus
}
#endif
