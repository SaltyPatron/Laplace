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

/* Both entry points preserve original scalar content and use Native's one
 * UAX decomposition. Normalization is an explicit recipe operation. */
int laplace_text_decomposer_run_source(
    const uint8_t* utf8,
    size_t         len,
    tier_tree_t**  out_tree);

/* Reconstruct the tree's canonical text from its tier-0 codepoint atoms and
 * require a byte-identical match with tier_tree_text(). A tree whose spans or
 * atoms do not encode the source buffer it owns is never admitted to hashing or
 * deposition.
 * Returns 0 on exact parity, -1 for an invalid tree argument, -2 for any
 * reconstruction/range mismatch. */
int laplace_text_decomposer_validate_roundtrip(const tier_tree_t* tree);

#ifdef __cplusplus
}
#endif
