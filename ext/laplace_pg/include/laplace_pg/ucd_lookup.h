/*
 * ucd_lookup.h — UcdLookupService public API (Track B / B14).
 *
 * O(1) in-process lookup of any Unicode codepoint's substrate identity:
 *   - 32-byte BLAKE3 entity_hash (atom identity)
 *   - 4-double S³ position (super-Fibonacci, UCA-collation-rank ordered)
 *   - 64-bit Hilbert index (linearization key)
 *   - 64-bit prime_flags (OR-combinable categorical bitmask)
 *
 * Backed by the laplace_generated static library (codepoint_table.c emitted
 * by Laplace.SeedTableGenerator). The generated table is ordered by the
 * substrate's UCA-driven super-Fibonacci sequencing (script → gc → UCA
 * primary weight → kRSUnicode for CJK → codepoint integer) — NOT by
 * codepoint integer — so a direct array index by codepoint does not work.
 *
 * This service builds a static inverse index (codepoint integer →
 * LAPLACE_CODEPOINT_TABLE position) lazily on first call. The inverse is
 * 4 bytes × 1,114,112 = ~4.46 MB per backend. PostgreSQL backends are
 * single-threaded so the lazy init needs no lock.
 *
 * Substrate invariants honored (CLAUDE.md):
 *   1 (content-addressed identity): hash returned is the entity's identity.
 *   2 (position is content-derived): s3 returned is super-Fibonacci output.
 *   3 (max dedup): single in-process index, single source for all callers.
 *   6 (prime flags as bit panels): prime_flags is the OR-combinable mask.
 */

#ifndef LAPLACE_UCD_LOOKUP_H
#define LAPLACE_UCD_LOOKUP_H

#include <stdint.h>

/* Match the generated codepoint_table.h definition exactly. */
#include "codepoint_table.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Lookup a codepoint's substrate entry. Returns NULL if `codepoint` is
 * outside [0, 0x110000) or if the codepoint is not present in the table
 * (cannot happen for the canonical full-Unicode-space build, but the
 * predicate is preserved for narrower-table builds and defensive use).
 *
 * The returned pointer is valid for the lifetime of the process — the
 * table lives in the .data segment of laplace_generated.lib.
 */
const laplace_codepoint_entry_t *
laplace_ucd_lookup(int32_t codepoint);

/* Predicate form: cheaper than calling lookup if the caller only needs to
 * test presence (skips the pointer dereference). */
int laplace_ucd_codepoint_exists(int32_t codepoint);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_UCD_LOOKUP_H */
