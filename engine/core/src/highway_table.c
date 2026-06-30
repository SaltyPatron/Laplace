#include "laplace/core/highway_table.h"
#include "laplace/core/highway_manifest.h"
#include "laplace/core/hash128.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#ifdef _WIN32
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#else
#  include <fcntl.h>
#  include <sys/mman.h>
#  include <sys/stat.h>
#  include <unistd.h>
#endif

/* Verify struct sizes match the binary layout written by codegen.
 * These are compile-time assertions and cost nothing at runtime. */
typedef char _hw_header_size_check [(sizeof(laplace_highway_header_t)  == 128) ? 1 : -1];
typedef char _hw_rec_size_check    [(sizeof(laplace_highway_rel_rec_t) ==  32) ? 1 : -1];
typedef char _hw_mask_size_check   [(sizeof(laplace_mask256_t)         ==  32) ? 1 : -1];

/* ── Global state ─────────────────────────────────────────────────────────── */

static struct {
    const uint8_t*                   base;
    size_t                           len;
    const laplace_highway_header_t*  header;
    const laplace_highway_rel_rec_t* records;     /* points into mmap */
    const laplace_mask256_t*         band_masks;  /* points into mmap */
    const char*                      strings;     /* points into mmap */
    /* Computed at load time: type_id for each relation (blake3(canonical_name)) */
    hash128_t type_id_cache[LAPLACE_HIGHWAY_REL_COUNT];
    /* Open-addressing hash table: type_id.lo & MASK → record ordinal, -1 = empty */
    int16_t   hash_bucket[HIGHWAY_BUCKET_SIZE];
} g_hw;

static int g_hw_loaded = 0;

/* ── Platform mmap ──────────────────────────────────────────────────────────── */

#ifdef _WIN32

static int hw_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(f, &sz) || sz.QuadPart < (LONGLONG)sizeof(laplace_highway_header_t)) {
        CloseHandle(f); return -1;
    }
    HANDLE m = CreateFileMappingA(f, NULL, PAGE_READONLY, 0, 0, NULL);
    CloseHandle(f);
    if (m == NULL) return -1;
    void* v = MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0);
    CloseHandle(m);
    if (v == NULL) return -1;
    *out_base = (const uint8_t*)v;
    *out_len  = (size_t)sz.QuadPart;
    return 0;
}

static void hw_unmap(const uint8_t* base, size_t len) {
    (void)len;
    UnmapViewOfFile((const void*)base);
}

#else

static int hw_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) != 0 || st.st_size < (off_t)sizeof(laplace_highway_header_t)) {
        close(fd); return -1;
    }
    size_t l = (size_t)st.st_size;
    void* m = mmap(NULL, l, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (m == MAP_FAILED) return -1;
    *out_base = (const uint8_t*)m;
    *out_len  = l;
    return 0;
}

static void hw_unmap(const uint8_t* base, size_t len) {
    munmap((void*)base, len);
}

#endif

/* ── Type-id computation (must match relation_law.c's type_id_from_canonical) ── */

static void type_id_for_canonical(const char* name, uint8_t name_len, hash128_t* out) {
    /* Content-addressed: entity hash = blake3(canonical_name_utf8_bytes).
     * Must match relation_law.c type_id_from_canonical and EntityTypeRegistry.Id() in C#. */
    hash128_blake3((const uint8_t*)name, (size_t)name_len, out);
}

/* ── Lifecycle ────────────────────────────────────────────────────────────── */

void highway_table_unload(void) {
    if (g_hw.base)
        hw_unmap(g_hw.base, g_hw.len);
    memset(&g_hw, 0, sizeof(g_hw));
    g_hw_loaded = 0;
}

int highway_table_is_loaded(void) {
    return g_hw_loaded;
}

int highway_table_load(const char* path) {
    if (!path) return -1;

    const uint8_t* base = NULL;
    size_t len = 0;
    if (hw_map(path, &base, &len) != 0) return -1;

    const laplace_highway_header_t* h = (const laplace_highway_header_t*)base;

    if (h->magic          != LAPLACE_HIGHWAY_MAGIC   ||
        h->format_version != LAPLACE_HIGHWAY_VERSION) {
        hw_unmap(base, len); return -2;
    }
    if (h->relation_count > LAPLACE_HIGHWAY_REL_COUNT ||
        h->band_count     > LAPLACE_HIGHWAY_BAND_COUNT) {
        hw_unmap(base, len); return -3;
    }

    /* Bounds checks: all section endpoints must fit within the file */
    uint64_t rel_end  = h->relations_offset    + h->relation_count * sizeof(laplace_highway_rel_rec_t);
    uint64_t band_end = h->band_masks_offset   + h->band_count     * sizeof(laplace_mask256_t);
    uint64_t str_end  = h->strings_offset      + h->strings_length;
    if (rel_end > len || band_end > len || str_end > len) {
        hw_unmap(base, len); return -3;
    }

    g_hw.base       = base;
    g_hw.len        = len;
    g_hw.header     = h;
    g_hw.records    = (const laplace_highway_rel_rec_t*)(base + h->relations_offset);
    g_hw.band_masks = (const laplace_mask256_t*)(base + h->band_masks_offset);
    g_hw.strings    = (const char*)(base + h->strings_offset);

    /* Compute type_ids and build the open-addressing hash table */
    memset(g_hw.hash_bucket, 0xFF, sizeof(g_hw.hash_bucket));  /* -1 (0xFFFF) = empty */
    for (uint64_t i = 0; i < h->relation_count; ++i) {
        const laplace_highway_rel_rec_t* rec = &g_hw.records[i];
        type_id_for_canonical(g_hw.strings + rec->name_off, rec->name_len,
                              &g_hw.type_id_cache[i]);

        size_t b = (size_t)(g_hw.type_id_cache[i].lo & HIGHWAY_BUCKET_MASK);
        while (g_hw.hash_bucket[b] >= 0)
            b = (b + 1) & HIGHWAY_BUCKET_MASK;
        g_hw.hash_bucket[b] = (int16_t)i;
    }

    g_hw_loaded = 1;
    return 0;
}

/* ── Lookups ──────────────────────────────────────────────────────────────── */

int highway_table_relation_by_hash(const hash128_t* type_id,
                                   uint8_t*         out_bit_pos,
                                   float*           out_rank,
                                   uint8_t*         out_band) {
    if (!g_hw_loaded || !type_id) return -1;
    size_t b = (size_t)(type_id->lo & HIGHWAY_BUCKET_MASK);
    for (size_t probe = 0; probe < HIGHWAY_BUCKET_SIZE; ++probe) {
        int16_t idx = g_hw.hash_bucket[b];
        if (idx < 0) return -1;   /* empty slot: not present */
        if (hash128_equals(type_id, &g_hw.type_id_cache[idx])) {
            const laplace_highway_rel_rec_t* rec = &g_hw.records[idx];
            if (out_bit_pos) *out_bit_pos = rec->bit_pos;
            if (out_rank)    *out_rank    = rec->rank;
            if (out_band)    *out_band    = rec->rank_band;
            return 0;
        }
        b = (b + 1) & HIGHWAY_BUCKET_MASK;
    }
    return -1;
}

int highway_table_relation_by_bit(uint8_t      bit_pos,
                                  const char** out_canonical,
                                  float*       out_rank,
                                  uint8_t*     out_band) {
    if (!g_hw_loaded) return -1;
    if ((uint64_t)bit_pos >= g_hw.header->relation_count) return -1;
    /* bit_pos is the record ordinal: relations are sorted alphabetically
     * and bit positions are assigned in that same order by codegen. */
    const laplace_highway_rel_rec_t* rec = &g_hw.records[bit_pos];
    if (out_canonical) *out_canonical = g_hw.strings + rec->name_off;
    if (out_rank)      *out_rank      = rec->rank;
    if (out_band)      *out_band      = rec->rank_band;
    return 0;
}

int highway_table_band_mask(uint8_t band, laplace_mask256_t* out_mask) {
    if (!g_hw_loaded || !out_mask) return -1;
    if ((uint64_t)band >= g_hw.header->band_count) return -1;
    *out_mask = g_hw.band_masks[band];
    return 0;
}

/* ── Mask utilities ────────────────────────────────────────────────────────── */

laplace_mask256_t highway_table_mask_or(laplace_mask256_t a, laplace_mask256_t b) {
    laplace_mask256_t r;
    r.w[0] = a.w[0] | b.w[0];
    r.w[1] = a.w[1] | b.w[1];
    r.w[2] = a.w[2] | b.w[2];
    r.w[3] = a.w[3] | b.w[3];
    return r;
}

laplace_mask256_t highway_table_mask_and(laplace_mask256_t a, laplace_mask256_t b) {
    laplace_mask256_t r;
    r.w[0] = a.w[0] & b.w[0];
    r.w[1] = a.w[1] & b.w[1];
    r.w[2] = a.w[2] & b.w[2];
    r.w[3] = a.w[3] & b.w[3];
    return r;
}

int highway_table_mask_test(const laplace_mask256_t* m, uint8_t bit) {
    if (!m) return 0;
    return (int)((m->w[bit / 64] >> (bit % 64)) & 1ULL);
}

void highway_table_mask_set(laplace_mask256_t* m, uint8_t bit) {
    if (!m) return;
    m->w[bit / 64] |= 1ULL << (bit % 64);
}

int highway_table_mask_any(const laplace_mask256_t* m) {
    if (!m) return 0;
    return (int)((m->w[0] | m->w[1] | m->w[2] | m->w[3]) != 0);
}
