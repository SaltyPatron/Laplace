/* One codepoint mapping: Native owns validation, IDs, coordinates and indexes. */
#include "laplace/core/codepoint_table.h"
#include "laplace/core/text_identity.h"
#include "laplace/core/utf8.h"
#include "laplace/tier0.h"
#include "platform.h"
#include <stddef.h>
#include <stdatomic.h>
#include <string.h>

_Static_assert(LAPLACE_CODEPOINT_COUNT == LP_TIER0_COUNT, "complete Native codepoint count");
_Static_assert(sizeof(codepoint_entry_t) == sizeof(lp_tier0_record), "Native codepoint ABI");
_Static_assert(offsetof(codepoint_entry_t, mantissa) == 16, "Native fixed coordinates");
_Static_assert(offsetof(codepoint_entry_t, uca_order) == 56, "Native collation rank");

static _Atomic(const lp_tier0_record *) active_floor;
static _Atomic int explicitly_unloaded;
static const lp_tier0_record *current_floor(void) {
    const lp_tier0_record *floor = atomic_load_explicit(&active_floor, memory_order_acquire);
    if (!floor && !atomic_load_explicit(&explicitly_unloaded, memory_order_acquire)) {
        floor = (const lp_tier0_record *)text_identity_scalar_floor();
        if (floor) atomic_store_explicit(&active_floor, floor, memory_order_release);
    }
    return floor;
}

int codepoint_table_load_perfcache(const char *path) {
    if (!path || !*path) return -1;
    size_t size = 0;
    const void *candidate = lp_map_file(path, 0, &size);
    if (!candidate) return -1;
    const size_t bytes = (size_t)LP_TIER0_COUNT * sizeof(lp_tier0_record);
    if (size != bytes) { lp_unmap_file(candidate, size); return -3; }
    const lp_tier0_record *canonical = text_identity_load_scalar_floor(path);
    if (!canonical) { lp_unmap_file(candidate, size); return -5; }
    /* An alias may select the same immutable floor. A second, divergent floor
     * cannot change coordinates underneath live text contexts and readers. */
    int same = memcmp(candidate, canonical, bytes) == 0;
    lp_unmap_file(candidate, size);
    if (!same) return -4;
    atomic_store_explicit(&explicitly_unloaded, 0, memory_order_release);
    atomic_store_explicit(&active_floor, canonical, memory_order_release);
    return 0;
}

void codepoint_table_unload(void) {
    atomic_store_explicit(&explicitly_unloaded, 1, memory_order_release);
    atomic_store_explicit(&active_floor, NULL, memory_order_release);
}
int codepoint_table_is_loaded(void) { return current_floor() != NULL; }

int codepoint_table_copy_receipt(hash128_t *out) {
    const lp_tier0_record *floor = current_floor();
    if (!floor || !out) return -1;
    uint8_t fingerprint[32]; lp_tier0_fingerprint(floor, fingerprint);
    memcpy(out, fingerprint, sizeof *out); return 0;
}

const codepoint_entry_t *codepoint_table_lookup(uint32_t scalar) {
    const lp_tier0_record *floor = current_floor();
    return floor ? (const codepoint_entry_t *)lp_tier0_at(floor, scalar) : NULL;
}

int codepoint_table_records(const codepoint_entry_t **records, uint64_t *count) {
    const lp_tier0_record *floor = current_floor();
    if (!floor) return -1;
    if (records) *records = (const codepoint_entry_t *)floor;
    if (count) *count = LP_TIER0_COUNT;
    return 0;
}

int codepoint_table_resolve_atom(uint32_t scalar, hash128_t *id, double coord[4], hilbert128_t *hilbert) {
    if (!current_floor()) return -1;
    return text_identity_atom(scalar, id, coord, hilbert);
}

int codepoint_table_lookup_id(const hash128_t *id, uint32_t *scalar) {
    const lp_tier0_record *floor = current_floor();
    if (!id || !floor) return -1;
    int64_t cp = lp_tier0_codepoint(floor, (const lp_id *)id);
    if (cp < 0) return -1;
    if (scalar) *scalar = (uint32_t)cp;
    return 0;
}

/* Native constructed its lookup indexes while validating this mapping. No
 * second reverse index, sort, allocation or per-load record copy is needed. */
int codepoint_table_prepare_id_index(size_t budget, size_t *added) {
    (void)budget; if (added) *added = 0; return current_floor() ? 0 : -1;
}
int codepoint_table_id_index_ready(void) { return current_floor() != NULL; }
size_t codepoint_table_id_index_bytes(void) { return 0; }

int codepoint_table_presence_bitmap(const hash128_t *ids, size_t count,
                                    uint8_t *bitmap, size_t bitmap_bytes) {
    size_t required = count / 8 + (count % 8 != 0);
    const lp_tier0_record *floor = current_floor();
    if ((count && !ids) || bitmap_bytes < required || (required && !bitmap) || !floor) return -1;
    if (required) memset(bitmap, 0, required);
    for (size_t i = 0; i < count; i++)
        if (lp_tier0_contains(floor, (const lp_id *)(ids + i)))
            bitmap[i >> 3] |= (uint8_t)(1u << (i & 7));
    return 0;
}

int laplace_text_is_all_whitespace(const uint8_t *utf8, size_t length) {
    if (!utf8 || !length) return 0;
    for (size_t at = 0; at < length;) {
        uint32_t cp; size_t consumed;
        if (laplace_utf8_decode(utf8 + at, length - at, &cp, &consumed)
            || !laplace_codepoint_is_whitespace(cp)) return 0;
        at += consumed;
    }
    return 1;
}
