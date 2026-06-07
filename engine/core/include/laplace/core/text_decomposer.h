#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

int laplace_text_decomposer_run(
    const uint8_t* utf8,
    size_t         len,
    tier_tree_t**  out_tree);

#ifdef __cplusplus
}
#endif
