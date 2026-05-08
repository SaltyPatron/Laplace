/*
 * ucd_lookup.c — UcdLookupService implementation (Track B / B14).
 *
 * O(1) lookup via lazy-built inverse index against LAPLACE_CODEPOINT_TABLE.
 * The table is ordered by super-Fibonacci-on-S³-via-UCA-collation; we build
 * a flat int32_t[0x110000] mapping codepoint→table-index on first call.
 *
 * Memory: ~4.46 MB per backend. Single-threaded init suffices because
 * PostgreSQL backends are single-threaded; laplace_native callers from
 * managed code through P/Invoke are also single-threaded per call.
 */

#include "laplace_pg/ucd_lookup.h"

#include <stddef.h>
#include <string.h>

#define LAPLACE_UCD_INDEX_SLOTS 0x110000  /* 1,114,112 — full Unicode codepoint space */

static int32_t s_codepoint_to_index[LAPLACE_UCD_INDEX_SLOTS];
static int     s_index_initialized = 0;

static void
laplace_ucd_lookup_initialize_inverse_index(void)
{
    if (s_index_initialized) {
        return;
    }

    /* Sentinel: -1 means "codepoint not present in the table". The full-
     * Unicode-space build covers every slot, but defensive sentinels keep
     * narrower builds (e.g., assigned-only) functional. */
    for (int32_t i = 0; i < LAPLACE_UCD_INDEX_SLOTS; ++i) {
        s_codepoint_to_index[i] = -1;
    }

    for (int32_t pos = 0; pos < LAPLACE_CODEPOINT_TABLE_COUNT; ++pos) {
        const int32_t cp = LAPLACE_CODEPOINT_TABLE[pos].codepoint;
        if (cp >= 0 && cp < LAPLACE_UCD_INDEX_SLOTS) {
            s_codepoint_to_index[cp] = pos;
        }
    }

    s_index_initialized = 1;
}

const laplace_codepoint_entry_t *
laplace_ucd_lookup(int32_t codepoint)
{
    if (codepoint < 0 || codepoint >= LAPLACE_UCD_INDEX_SLOTS) {
        return NULL;
    }
    laplace_ucd_lookup_initialize_inverse_index();
    const int32_t pos = s_codepoint_to_index[codepoint];
    if (pos < 0) {
        return NULL;
    }
    return &LAPLACE_CODEPOINT_TABLE[pos];
}

int
laplace_ucd_codepoint_exists(int32_t codepoint)
{
    if (codepoint < 0 || codepoint >= LAPLACE_UCD_INDEX_SLOTS) {
        return 0;
    }
    laplace_ucd_lookup_initialize_inverse_index();
    return s_codepoint_to_index[codepoint] >= 0;
}
