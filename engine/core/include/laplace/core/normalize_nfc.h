#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* NFC normalization (UAX #15) per ADR 0047. Input + output are
 * codepoint arrays (caller decodes UTF-8 first).
 *
 * Algorithm:
 *   1. Decompose: replace each codepoint with its full canonical
 *      decomposition (recursive via the generated decomp table).
 *      Hangul syllable decomposition (UAX #15 §1.3) is handled
 *      arithmetically.
 *   2. Canonical reorder: within each run of non-starters (CCC > 0),
 *      sort by CCC ascending (stable).
 *   3. Canonical compose: walk; for each starter S followed by
 *      non-starters and a candidate, try to compose (S, X) via the
 *      generated compose table (with excluded codepoints already
 *      filtered out at codegen time). Hangul composition arithmetic
 *      handled here too.
 *
 * Returns the NFC length (number of codepoints written to `out`). If
 * `out_cap` is insufficient, writes nothing and returns the required
 * length (caller can then resize + retry).
 *
 * Pure: zero global state, deterministic per LAPLACE_UNICODE_VERSION. */
size_t laplace_normalize_nfc(
    const uint32_t* in,
    size_t          in_len,
    uint32_t*       out,
    size_t          out_cap);

#ifdef __cplusplus
}
#endif
