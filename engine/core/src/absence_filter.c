#include "laplace/core/absence_filter.h"

#include <math.h>
#include <stdatomic.h>
#include <string.h>
#include <stdlib.h>

/* ln(2) and ln(2)^2 — the Bloom sizing constants. */
#define LAPLACE_LN2    0.6931471805599453
#define LAPLACE_LN2_SQ 0.4804530139182014

#define LAPLACE_ABSENCE_MAX_K 32u

static uint64_t round_up_pow2_u64(uint64_t v) {
    if (v <= 1) return 1;
    v--;
    v |= v >> 1;  v |= v >> 2;  v |= v >> 4;
    v |= v >> 8;  v |= v >> 16; v |= v >> 32;
    return v + 1;
}

int laplace_absence_create(laplace_absence_filter_t* f, uint64_t capacity, double fpr) {
    if (!f || capacity == 0 || !(fpr > 0.0) || !(fpr < 1.0)) return -1;

    /* m = -n ln(p) / ln(2)^2, rounded up to a power of two so the modulo is a mask.
     * Rounding up only ever LOWERS the realised false-positive rate. */
    double   m_ideal = -((double)capacity) * log(fpr) / LAPLACE_LN2_SQ;
    uint64_t m       = round_up_pow2_u64((uint64_t)(m_ideal + 1.0));
    if (m < 64) m = 64;

    /* k = (m/n) ln 2, clamped to at least one probe and a sane ceiling. */
    double k_ideal = ((double)m / (double)capacity) * LAPLACE_LN2;
    uint32_t k = (uint32_t)(k_ideal + 0.5);
    if (k < 1) k = 1;
    if (k > LAPLACE_ABSENCE_MAX_K) k = LAPLACE_ABSENCE_MAX_K;

    size_t words = (size_t)(m / 64);
    uint64_t* bits = (uint64_t*)calloc(words, sizeof(uint64_t));
    if (!bits) return -2;

    memset(f, 0, sizeof(*f));
    f->hdr.magic          = LAPLACE_ABSENCE_MAGIC;
    f->hdr.format_version = LAPLACE_ABSENCE_VERSION;
    f->hdr.bit_count      = m;
    f->hdr.hash_count     = k;
    f->hdr.capacity       = capacity;
    f->hdr.inserted       = 0;
    f->bits               = bits;
    f->word_count         = words;
    f->owns_bits          = true;
    return 0;
}

void laplace_absence_destroy(laplace_absence_filter_t* f) {
    if (!f) return;
    if (f->owns_bits && f->bits) free(f->bits);
    f->bits = NULL;
    f->word_count = 0;
    f->owns_bits = false;
}

/*
 * Kirsch-Mitzenmacher double hashing over the id's own halves. The ids are
 * BLAKE3 digests, so lo/hi are already uniform — no extra mixing buys anything.
 * h2 is forced ODD so that it is coprime with the power-of-two m; an even stride
 * would visit only a subset of slots and, at the degenerate h2 = 0, would set the
 * same bit k times and silently collapse k to 1.
 */
static inline void probe_pair(const hash128_t* id, uint64_t* h1, uint64_t* h2) {
    *h1 = id->lo;
    *h2 = id->hi | 1ull;
}

void laplace_absence_add(laplace_absence_filter_t* f, const hash128_t* id) {
    if (!f || !f->bits || !id) return;
    const uint64_t mask = f->hdr.bit_count - 1;
    uint64_t h1, h2;
    probe_pair(id, &h1, &h2);

    for (uint32_t i = 0; i < f->hdr.hash_count; ++i) {
        uint64_t bit  = (h1 + (uint64_t)i * h2) & mask;
        uint64_t word = bit >> 6;
        uint64_t m    = 1ull << (bit & 63);
        /* Relaxed is sufficient: bits are only ever set, never cleared, so any
         * interleaving converges on the same word and a concurrent reader either
         * sees the bit or takes the (always safe) probe path. */
        atomic_fetch_or_explicit(
            (_Atomic uint64_t*)&f->bits[word], m, memory_order_relaxed);
    }
    atomic_fetch_add_explicit(
        (_Atomic uint64_t*)&f->hdr.inserted, 1ull, memory_order_relaxed);
}

bool laplace_absence_maybe_present(const laplace_absence_filter_t* f, const hash128_t* id) {
    /* A filter that does not exist proves nothing — say "maybe" so the caller
     * probes. Absence must never be claimed by accident. */
    if (!f || !f->bits || !id) return true;

    const uint64_t mask = f->hdr.bit_count - 1;
    uint64_t h1, h2;
    probe_pair(id, &h1, &h2);

    for (uint32_t i = 0; i < f->hdr.hash_count; ++i) {
        uint64_t bit  = (h1 + (uint64_t)i * h2) & mask;
        uint64_t word = bit >> 6;
        uint64_t m    = 1ull << (bit & 63);
        uint64_t w    = atomic_load_explicit(
            (const _Atomic uint64_t*)&f->bits[word], memory_order_relaxed);
        if ((w & m) == 0) return false;   /* PROOF of absence */
    }
    return true;
}

size_t laplace_absence_blob_size(const laplace_absence_filter_t* f) {
    if (!f) return 0;
    return sizeof(laplace_absence_header_t) + f->word_count * sizeof(uint64_t);
}

int laplace_absence_save(const laplace_absence_filter_t* f, void* dst, size_t dst_len) {
    if (!f || !f->bits || !dst) return -1;
    size_t need = laplace_absence_blob_size(f);
    if (dst_len < need) return -1;

    memcpy(dst, &f->hdr, sizeof(f->hdr));
    memcpy((uint8_t*)dst + sizeof(f->hdr), f->bits, f->word_count * sizeof(uint64_t));
    return 0;
}

int laplace_absence_attach(laplace_absence_filter_t* f, void* src, size_t src_len) {
    if (!f || !src || src_len < sizeof(laplace_absence_header_t)) return -1;

    laplace_absence_header_t hdr;
    memcpy(&hdr, src, sizeof(hdr));
    if (hdr.magic != LAPLACE_ABSENCE_MAGIC) return -3;
    if (hdr.format_version != LAPLACE_ABSENCE_VERSION) return -3;
    if (hdr.bit_count < 64 || (hdr.bit_count & (hdr.bit_count - 1)) != 0) return -3;
    if (hdr.hash_count < 1 || hdr.hash_count > LAPLACE_ABSENCE_MAX_K) return -3;

    size_t words = (size_t)(hdr.bit_count / 64);
    if (src_len < sizeof(hdr) + words * sizeof(uint64_t)) return -3;

    memset(f, 0, sizeof(*f));
    f->hdr        = hdr;
    f->bits       = (uint64_t*)((uint8_t*)src + sizeof(hdr));
    f->word_count = words;
    f->owns_bits  = false;
    return 0;
}

double laplace_absence_estimated_fpr(const laplace_absence_filter_t* f) {
    if (!f || !f->bits || f->hdr.bit_count == 0) return 1.0;
    uint64_t n = atomic_load_explicit(
        (const _Atomic uint64_t*)&f->hdr.inserted, memory_order_relaxed);
    if (n == 0) return 0.0;
    double k = (double)f->hdr.hash_count;
    double m = (double)f->hdr.bit_count;
    /* (1 - e^(-kn/m))^k */
    double occupancy = 1.0 - exp(-(k * (double)n) / m);
    return pow(occupancy, k);
}
