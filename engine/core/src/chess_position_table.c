#include "laplace/core/chess_position_table.h"
#include "laplace/core/chess_perfcache_format.h"
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
    const uint8_t*                          base;
    size_t                                  length;
    const laplace_chess_perfcache_header_t* header;
    const laplace_chess_perfcache_record_t* records;
    uint64_t                                record_count;
} g_ch = {0};

#ifdef _WIN32

static int ch_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(f, &sz)
        || sz.QuadPart < (LONGLONG)(LAPLACE_CHESS_PERFCACHE_HEADER_SIZE
                                    + LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES)) {
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

static void ch_unmap(const uint8_t* base, size_t len) {
    (void)len;
    UnmapViewOfFile((const void*)base);
}

#else

static int ch_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) != 0
        || st.st_size < (off_t)(LAPLACE_CHESS_PERFCACHE_HEADER_SIZE
                                + LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES)) {
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

static void ch_unmap(const uint8_t* base, size_t len) {
    munmap((void*)base, len);
}

#endif

void chess_position_table_unload(void) {
    if (g_ch.base) ch_unmap(g_ch.base, g_ch.length);
    memset(&g_ch, 0, sizeof(g_ch));
}

int chess_position_table_is_loaded(void) {
    return g_ch.records != NULL;
}

int chess_position_table_load(const char* path) {
    if (path == NULL) return -1;

    const uint8_t* base = NULL;
    size_t len = 0;
    if (ch_map(path, &base, &len) != 0) return -1;

    const laplace_chess_perfcache_header_t* h =
        (const laplace_chess_perfcache_header_t*)base;

    if (h->magic != LAPLACE_CHESS_PERFCACHE_MAGIC
        || h->format_version != LAPLACE_CHESS_PERFCACHE_VERSION) {
        ch_unmap(base, len);
        return -2;
    }
    if (h->record_size != LAPLACE_CHESS_PERFCACHE_RECORD_SIZE
        || h->records_offset < LAPLACE_CHESS_PERFCACHE_HEADER_SIZE) {
        ch_unmap(base, len);
        return -3;
    }

    uint64_t body_end = len - LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES;
    if (h->records_offset + h->record_count * h->record_size > body_end) {
        ch_unmap(base, len);
        return -3;
    }

    hash128_t crc;
    hash128_blake3(base, (size_t)body_end, &crc);
    const hash128_t* stored = (const hash128_t*)(base + body_end);
    if (memcmp(&crc, stored, sizeof(hash128_t)) != 0) {
        ch_unmap(base, len);
        return -4;
    }

    chess_position_table_unload();
    g_ch.base = base;
    g_ch.length = len;
    g_ch.header = h;
    g_ch.records = (const laplace_chess_perfcache_record_t*)(base + h->records_offset);
    g_ch.record_count = h->record_count;
    return 0;
}

const laplace_chess_perfcache_record_t*
chess_position_table_lookup(const hash128_t* id) {
    if (g_ch.records == NULL || id == NULL || g_ch.record_count == 0) return NULL;

    uint64_t lo = 0, hi = g_ch.record_count - 1;
    while (lo <= hi) {
        uint64_t mid = lo + ((hi - lo) >> 1);
        int cmp = hash128_compare(&g_ch.records[mid].id, id);
        if (cmp == 0) return &g_ch.records[mid];
        if (cmp < 0) {
            lo = mid + 1;
        } else {
            if (mid == 0) break;
            hi = mid - 1;
        }
    }
    return NULL;
}

int chess_position_table_lookup_geom(const hash128_t* id,
                                     double out_coord[4],
                                     hilbert128_t* out_hb,
                                     uint32_t* out_n,
                                     uint8_t* out_tier) {
    const laplace_chess_perfcache_record_t* r = chess_position_table_lookup(id);
    if (r == NULL) return -1;
    if (out_coord) {
        out_coord[0] = r->coord[0];
        out_coord[1] = r->coord[1];
        out_coord[2] = r->coord[2];
        out_coord[3] = r->coord[3];
    }
    if (out_hb) *out_hb = r->hilbert;
    if (out_n) *out_n = r->n;
    if (out_tier) *out_tier = r->tier;
    return 0;
}

int chess_position_table_record_count(uint64_t* out_count) {
    if (g_ch.records == NULL) return -1;
    if (out_count) *out_count = g_ch.record_count;
    return 0;
}
