#include "laplace/core/codepoint_table.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/hash128.h"
#include "laplace/core/utf8.h"

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/*
 * Keep the runtime load boundary pinned to the same Unicode release that owns
 * the shipped T0 artifact. The generator already writes this exact release into
 * laplace_perfcache_header_t::ucd_version; accepting a different header would
 * silently change segmentation and therefore content identity.
 */
#define LAPLACE_EXPECTED_UCD_VERSION "17.0.0"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

static struct {
    const uint8_t*                     base;
    size_t                             length;
    const laplace_perfcache_header_t*  header;
    const laplace_perfcache_record_t*  records;
    const laplace_perfcache_decomp_t*  decomp_recs;
    const uint32_t*                    decomp_data;
    const laplace_perfcache_compose_t* compose_recs;
    uint64_t                           record_count;
    uint64_t                           decomp_count;
    uint64_t                           compose_count;
    uint32_t*                          rev_idx;
    uint64_t                           rev_count;
} g_pc = {0};

#ifdef _WIN32

static int pc_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(f, &sz)
        || sz.QuadPart < (LONGLONG)(sizeof(laplace_perfcache_header_t)
                                    + LAPLACE_PERFCACHE_TRAILER_BYTES)) {
        CloseHandle(f);
        return -1;
    }
    HANDLE m = CreateFileMappingA(f, NULL, PAGE_READONLY, 0, 0, NULL);
    CloseHandle(f);
    if (m == NULL) return -1;
    void* v = MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0);
    CloseHandle(m);
    if (v == NULL) return -1;
    *out_base = (const uint8_t*)v;
    *out_len = (size_t)sz.QuadPart;
    return 0;
}

static void pc_unmap(const uint8_t* base, size_t len) {
    (void)len;
    UnmapViewOfFile((const void*)base);
}

#else

static int pc_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;

    struct stat st;
    if (fstat(fd, &st) != 0 || st.st_size < (off_t)(sizeof(laplace_perfcache_header_t)
                                                    + LAPLACE_PERFCACHE_TRAILER_BYTES)) {
        close(fd);
        return -1;
    }
    size_t len = (size_t)st.st_size;
    void* m = mmap(NULL, len, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (m == MAP_FAILED) return -1;
    *out_base = (const uint8_t*)m;
    *out_len = len;
    return 0;
}

static void pc_unmap(const uint8_t* base, size_t len) {
    munmap((void*)base, len);
}

#endif

void codepoint_table_unload(void) {
    if (g_pc.base) {
        pc_unmap(g_pc.base, g_pc.length);
    }
    if (g_pc.rev_idx) {
        free(g_pc.rev_idx);
    }
    memset(&g_pc, 0, sizeof(g_pc));
}

int codepoint_table_is_loaded(void) {
    return g_pc.records != NULL;
}

int codepoint_table_copy_receipt(hash128_t* out_receipt) {
    if (out_receipt == NULL || g_pc.records == NULL) return -1;
    memcpy(out_receipt, g_pc.base + g_pc.length - LAPLACE_PERFCACHE_TRAILER_BYTES,
        sizeof(*out_receipt));
    return 0;
}

static int pc_version_matches(const char actual[8], const char* expected) {
    const size_t expected_len = strlen(expected);
    if (expected_len >= 8) return 0;
    return memcmp(actual, expected, expected_len) == 0
        && actual[expected_len] == '\0';
}

int codepoint_table_load_perfcache(const char* path) {
    if (path == NULL) return -1;

    const uint8_t* base = NULL;
    size_t len = 0;
    if (pc_map(path, &base, &len) != 0) return -1;

    const laplace_perfcache_header_t* h = (const laplace_perfcache_header_t*)base;

    if (h->magic != LAPLACE_PERFCACHE_MAGIC || h->format_version != LAPLACE_PERFCACHE_VERSION) {
        pc_unmap(base, len); return -2;
    }
    if (!pc_version_matches(h->ucd_version, LAPLACE_EXPECTED_UCD_VERSION)) {
        pc_unmap(base, len); return -5;
    }
    if ((h->record_count != 0x80ull
         && h->record_count != 0x10000ull
         && h->record_count != (uint64_t)LAPLACE_PERFCACHE_RECORD_COUNT)
        || h->record_size != sizeof(laplace_perfcache_record_t)
        || h->record_count > (uint64_t)LAPLACE_PERFCACHE_RECORD_COUNT) {
        pc_unmap(base, len); return -3;
    }
    uint64_t body_end = len - LAPLACE_PERFCACHE_TRAILER_BYTES;
    if (h->records_offset + h->record_count * h->record_size > body_end
        || h->decomp_records_offset + h->decomp_record_count * sizeof(laplace_perfcache_decomp_t) > body_end
        || h->decomp_data_offset + h->decomp_data_count * sizeof(uint32_t) > body_end
        || h->compose_records_offset + h->compose_record_count * sizeof(laplace_perfcache_compose_t) > body_end) {
        pc_unmap(base, len); return -3;
    }

    hash128_t crc;
    hash128_blake3(base, (size_t)body_end, &crc);
    const hash128_t* stored = (const hash128_t*)(base + body_end);
    if (memcmp(&crc, stored, sizeof(hash128_t)) != 0) {
        pc_unmap(base, len); return -4;
    }

    codepoint_table_unload();
    g_pc.base = base;
    g_pc.length = len;
    g_pc.header = h;
    g_pc.records      = (const laplace_perfcache_record_t*) (base + h->records_offset);
    g_pc.decomp_recs  = (const laplace_perfcache_decomp_t*) (base + h->decomp_records_offset);
    g_pc.decomp_data  = (const uint32_t*)                   (base + h->decomp_data_offset);
    g_pc.compose_recs = (const laplace_perfcache_compose_t*)(base + h->compose_records_offset);
    g_pc.record_count  = h->record_count;
    g_pc.decomp_count  = h->decomp_record_count;
    g_pc.compose_count = h->compose_record_count;
    return 0;
}

const codepoint_entry_t* codepoint_table_lookup(uint32_t cp) {
    if (g_pc.records == NULL || cp >= g_pc.record_count) return NULL;
    return &g_pc.records[cp];
}

int codepoint_table_records(const codepoint_entry_t** out_records, uint64_t* out_count) {
    if (g_pc.records == NULL) return -1;
    if (out_records) *out_records = g_pc.records;
    if (out_count)   *out_count = g_pc.record_count;
    return 0;
}

uint8_t codepoint_table_gb(uint32_t cp) {
    const codepoint_entry_t* e = codepoint_table_lookup(cp);
    return e ? laplace_pc_gb(e->flags) : 0;
}
uint8_t codepoint_table_wb(uint32_t cp) {
    const codepoint_entry_t* e = codepoint_table_lookup(cp);
    return e ? laplace_pc_wb(e->flags) : 0;
}
uint8_t codepoint_table_sb(uint32_t cp) {
    const codepoint_entry_t* e = codepoint_table_lookup(cp);
    return e ? laplace_pc_sb(e->flags) : 0;
}
uint8_t codepoint_table_incb(uint32_t cp) {
    const codepoint_entry_t* e = codepoint_table_lookup(cp);
    return e ? laplace_pc_incb(e->flags) : 0;
}
uint8_t codepoint_table_ccc(uint32_t cp) {
    const codepoint_entry_t* e = codepoint_table_lookup(cp);
    return e ? laplace_pc_ccc(e->flags) : 0;
}

int codepoint_table_decompose(uint32_t cp, const uint32_t** out_seq, uint32_t* out_len) {
    if (g_pc.decomp_recs == NULL || g_pc.decomp_count == 0) return 0;
    size_t lo = 0, hi = g_pc.decomp_count;
    while (lo < hi) {
        size_t mid = lo + ((hi - lo) >> 1);
        const laplace_perfcache_decomp_t* r = &g_pc.decomp_recs[mid];
        if (cp < r->cp) hi = mid;
        else if (cp > r->cp) lo = mid + 1;
        else {
            *out_seq = &g_pc.decomp_data[r->start_idx];
            *out_len = r->length;
            return 1;
        }
    }
    return 0;
}

int codepoint_table_compose(uint32_t first, uint32_t second, uint32_t* out_composed) {
    if (g_pc.compose_recs == NULL || g_pc.compose_count == 0) return 0;
    size_t lo = 0, hi = g_pc.compose_count;
    while (lo < hi) {
        size_t mid = lo + ((hi - lo) >> 1);
        const laplace_perfcache_compose_t* r = &g_pc.compose_recs[mid];
        if (first < r->first || (first == r->first && second < r->second)) hi = mid;
        else if (first > r->first || (first == r->first && second > r->second)) lo = mid + 1;
        else { *out_composed = r->composed; return 1; }
    }
    return 0;
}

int codepoint_table_resolve_atom(uint32_t atom, hash128_t* out_id,
                                 double out_coord[4], hilbert128_t* out_hb) {
    if (!out_id || !out_coord || !out_hb) return -1;
    const codepoint_entry_t* e = codepoint_table_lookup(atom);
    if (!e) return -1;
    *out_id = e->hash;
    out_coord[0] = e->coord[0];
    out_coord[1] = e->coord[1];
    out_coord[2] = e->coord[2];
    out_coord[3] = e->coord[3];
    *out_hb = e->hilbert;
    return 0;
}

static int rev_cmp(uint32_t a, uint32_t b) {
    return memcmp(&g_pc.records[a].hash, &g_pc.records[b].hash, sizeof(hash128_t));
}

static void rev_sift(uint32_t* idx, size_t count, size_t root) {
    while (root < count / 2u) {
        size_t child = root * 2u + 1u;
        if (child + 1u < count && rev_cmp(idx[child], idx[child + 1u]) < 0) ++child;
        if (rev_cmp(idx[root], idx[child]) >= 0) return;
        const uint32_t temporary = idx[root]; idx[root] = idx[child]; idx[child] = temporary;
        root = child;
    }
}

int codepoint_table_prepare_id_index(size_t maximum_additional_bytes, size_t* out_added_bytes) {
    if (out_added_bytes != NULL) *out_added_bytes = 0u;
    if (g_pc.rev_idx) return 0;
    if (!g_pc.records || g_pc.record_count == 0) return -1;
    if (g_pc.record_count > UINT32_MAX || g_pc.record_count > SIZE_MAX / sizeof(uint32_t)) return -2;
    const size_t bytes = (size_t)g_pc.record_count * sizeof(uint32_t);
    if (bytes > maximum_additional_bytes) return -2;
    uint32_t* idx = (uint32_t*)malloc(bytes);
    if (!idx) return -2;
    for (uint64_t i = 0; i < g_pc.record_count; ++i)
        idx[i] = (uint32_t)i;
    const size_t count = (size_t)g_pc.record_count;
    for (size_t i = count / 2u; i != 0u; --i) rev_sift(idx, count, i - 1u);
    for (size_t end = count; end > 1u; --end) {
        const uint32_t temporary = idx[0]; idx[0] = idx[end - 1u]; idx[end - 1u] = temporary;
        rev_sift(idx, end - 1u, 0u);
    }
    g_pc.rev_idx = idx;
    g_pc.rev_count = g_pc.record_count;
    if (out_added_bytes != NULL) *out_added_bytes = bytes;
    return 0;
}

int codepoint_table_id_index_ready(void) { return g_pc.rev_idx != NULL; }

size_t codepoint_table_id_index_bytes(void) {
    return g_pc.rev_idx == NULL ? 0u : (size_t)g_pc.rev_count * sizeof(uint32_t);
}

int codepoint_table_lookup_id(const hash128_t* id, uint32_t* out_cp) {
    if (!id || !g_pc.records) return -1;
    (void)codepoint_table_prepare_id_index(SIZE_MAX, NULL);
    if (!g_pc.rev_idx) return -1;

    uint64_t lo = 0, hi = g_pc.rev_count;
    while (lo < hi) {
        uint64_t mid = lo + ((hi - lo) >> 1);
        uint32_t cp = g_pc.rev_idx[mid];
        int c = memcmp(id, &g_pc.records[cp].hash, sizeof(hash128_t));
        if (c < 0) hi = mid;
        else if (c > 0) lo = mid + 1;
        else {
            if (cp >= 0xD800u && cp <= 0xDFFFu) return -1;
            if (out_cp) *out_cp = cp;
            return 0;
        }
    }
    return -1;
}

int laplace_codepoint_is_whitespace(uint32_t cp) {
    const codepoint_entry_t* e = codepoint_table_lookup(cp);
    return e ? (laplace_pc_white_space(e->flags) != 0u) : 0;
}

int codepoint_table_presence_bitmap(const hash128_t* ids, size_t count,
                                    uint8_t* bitmap, size_t bitmap_bytes) {
    size_t required = count / 8 + (count % 8 != 0);
    if (bitmap_bytes < required || (required && !bitmap) || (count && !ids))
        return -1;
    if (required) memset(bitmap, 0, required);
    if (!codepoint_table_is_loaded()) return -1;
    for (size_t i = 0; i < count; ++i)
        if (codepoint_table_lookup_id(&ids[i], NULL) == 0)
            bitmap[i >> 3] |= (uint8_t)(1u << (i & 7));
    return 0;
}

int laplace_text_is_all_whitespace(const uint8_t* utf8, size_t len) {
    if (utf8 == NULL || len == 0) return 0;
    size_t off = 0;
    while (off < len) {
        uint32_t cp;
        size_t   consumed;
        if (laplace_utf8_decode(utf8 + off, len - off, &cp, &consumed) != 0)
            return 0;
        if (!laplace_codepoint_is_whitespace(cp))
            return 0;
        off += consumed;
    }
    return 1;
}
