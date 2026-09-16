#pragma once

#include <stddef.h>
#include <stdint.h>
#include "laplace/core/hash128.h"
#include "laplace/core/attestation_engine.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Project the explicit annotations of one witnessed UD parse into attributed
 * canonical relation cells. The parse trajectory remains the exact record of
 * token order, heads, positions, gaps, MWTs and annotation scope; this operation
 * does not manufacture PRECEDES/FOLLOWS or semantic claims from ordinary text.
 *
 * Canonical forms/POS/features/lemmas remain unchanged across sources. Context
 * is the source occurrence of the complete parse, never part of word identity.
 * Repeated identical annotations on one token/edge count once. Distinct token
 * occurrences contributing the same cell are aggregated into observation_count,
 * not presented as independent witnesses. Retrying the same parse occurrence
 * therefore builds the same witness identities and counts.
 *
 * language_misc_key is the parser's resolved identifier for the UD Lang field.
 * An explicit token language overrides the parse language for that occurrence.
 * No label rendering or language-specific routing occurs here.
 *
 * One output allocation of flat_count staged rows is sufficient. out_count is
 * zero on failure, and no output prefix is publishable unless the return is 0.
 * Return -1 for arguments, -2 for malformed parse, -3 for allocation/capacity,
 * -4 for unavailable relation law, -5 for witness construction failure.
 */
int laplace_ud_witness_build(
    const hash128_t *flat, size_t flat_count,
    const hash128_t *source, const hash128_t *parse_occurrence,
    const hash128_t *language_misc_key,
    double witness_weight, int64_t now_unix_us,
    laplace_attestation_staged_t *out, size_t out_capacity,
    size_t *out_count);

#ifdef __cplusplus
}
#endif
