#include "laplace/core/codepoint_table.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/hash128.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

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
    memset(&g_pc, 0, sizeof(g_pc));
}

int codepoint_table_is_loaded(void) {
    return g_pc.records != NULL;
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
    if (h->record_count != LAPLACE_PERFCACHE_RECORD_COUNT
        || h->record_size != sizeof(laplace_perfcache_record_t)) {
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
