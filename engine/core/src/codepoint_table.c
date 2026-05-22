#include "laplace/core/codepoint_table.h"

#include <stddef.h>
#include <string.h>

/* Real implementations land Chunk 3 — perf-cache + DB-seed sibling
 * derivation from Unicode UCD per ADR 0006. Stubs satisfy linkage. */

int codepoint_table_build_from_ucd(const char* ucd_path, codepoint_entry_t* out) {
    (void)ucd_path; (void)out;
    return -1;
}

int codepoint_table_load_perfcache(const char* path) {
    (void)path;
    return -1;
}

const codepoint_entry_t* codepoint_table_lookup(uint32_t codepoint) {
    (void)codepoint;
    return NULL;
}
