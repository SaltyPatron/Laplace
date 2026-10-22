/* Native's self-contained highway mapping; relation selectors are manifest data. */
#include "laplace/core/highway_table.h"
#include "laplace/core/highway_manifest.h"
#include "laplace/core/relation_law.h"
#include "laplace/highway.h"
#include <stdatomic.h>
#include <string.h>

static _Atomic(const lp_highway *) active_highway;
static const lp_highway *current_highway(void) {
    return atomic_load_explicit(&active_highway, memory_order_acquire);
}
int highway_table_load(const char *path) {
    if (!path || !*path) return -1;
    const lp_highway *mapped = lp_highway_map(path);
    if (!mapped) return -2;
    atomic_store_explicit(&active_highway, mapped, memory_order_release);
    return 0;
}
void highway_table_unload(void) {
    atomic_store_explicit(&active_highway, NULL, memory_order_release);
}
int highway_table_is_loaded(void) { return current_highway() != NULL; }
int highway_table_records(const codepoint_entry_t **records, uint64_t *count) {
    const lp_highway *h = current_highway();
    if (!h) return -1;
    if (records) *records = (const codepoint_entry_t *)h->rec;
    if (count) *count = h->nrec;
    return 0;
}
const codepoint_entry_t *highway_table_find(const hash128_t *id) {
    const lp_highway *h = current_highway();
    return h && id ? (const codepoint_entry_t *)lp_highway_find(h, (const lp_id *)id) : NULL;
}
const codepoint_entry_t *highway_table_at(const char *list, uint32_t slot) {
    const lp_highway *h = current_highway();
    return h && list ? (const codepoint_entry_t *)lp_highway_at(h, lp_highway_list(h, list), slot) : NULL;
}
int64_t highway_table_key(const char *list, const char *key) {
    const lp_highway *h = current_highway();
    return h && list && key ? lp_highway_key(h, lp_highway_list(h, list), key) : -1;
}
int highway_table_fingerprint(uint8_t out[32]) {
    const lp_highway *h = current_highway();
    if (!h || !out) return -1;
    lp_highway_fingerprint(h, out); return 0;
}
int highway_table_edge_span(const char *from, uint32_t slot, const char *to,
                            const void **edges, uint64_t *count) {
    const lp_highway *h = current_highway(); const lp_edge *pairs = NULL;
    if (!h || !from || !to || !edges || !count) return -1;
    *count = lp_highway_edges(h, from, slot, to, &pairs);
    *edges = pairs; return 0;
}

/* The relation table and selector metadata share codegen's canonical order. */
int highway_table_relation_by_hash(const hash128_t *id, uint8_t *bit, float *rank, uint8_t *band) {
    const laplace_relation_def_t *definition = NULL;
    if (!current_highway() || !id || laplace_relation_lookup(id, &definition) || !definition) return -1;
    size_t index = (size_t)(definition - laplace_relation_table);
    if (index >= sizeof highway_relation_selectors / sizeof *highway_relation_selectors) return -1;
    if (bit) *bit = highway_relation_selectors[index].bit;
    if (rank) *rank = (float)definition->rank;
    if (band) *band = highway_relation_selectors[index].band;
    return 0;
}
int highway_table_relation_by_bit(uint8_t bit, const char **name, float *rank, uint8_t *band) {
    if (!current_highway()) return -1;
    for (size_t index = 0; index < laplace_relation_table_count; index++) {
        if (highway_relation_selectors[index].bit != bit) continue;
        if (name) *name = laplace_relation_table[index].canonical;
        if (rank) *rank = (float)laplace_relation_table[index].rank;
        if (band) *band = highway_relation_selectors[index].band;
        return 0;
    }
    return -1;
}
int highway_table_band_mask(uint8_t band, laplace_mask256_t *mask) {
    if (!current_highway() || !mask || band >= LAPLACE_HIGHWAY_BAND_COUNT) return -1;
    memcpy(mask, highway_relation_band_masks[band], sizeof *mask); return 0;
}
laplace_mask256_t highway_table_mask_or(laplace_mask256_t a, laplace_mask256_t b) {
    for (size_t i = 0; i < 4; i++) a.w[i] |= b.w[i];
    return a;
}
laplace_mask256_t highway_table_mask_and(laplace_mask256_t a, laplace_mask256_t b) {
    for (size_t i = 0; i < 4; i++) a.w[i] &= b.w[i];
    return a;
}
int highway_table_mask_test(const laplace_mask256_t *mask, uint8_t bit) {
    return mask ? (int)((mask->w[bit / 64] >> (bit % 64)) & 1) : 0;
}
void highway_table_mask_set(laplace_mask256_t *mask, uint8_t bit) {
    if (mask) mask->w[bit / 64] |= UINT64_C(1) << (bit % 64);
}
int highway_table_mask_any(const laplace_mask256_t *mask) {
    return mask && (mask->w[0] | mask->w[1] | mask->w[2] | mask->w[3]);
}
