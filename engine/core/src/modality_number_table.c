#include "laplace/core/modality_number_table.h"
#include "laplace/core/modality_number_perfcache_format.h"
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
    const uint8_t*                                       base;
    size_t                                               length;
    const laplace_modality_number_perfcache_header_t*    header;
    const laplace_modality_number_perfcache_record_t*    records;
    uint64_t                                             record_count;
} g_mn = {0};

#ifdef _WIN32

static int mn_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(f, &sz)
        || sz.QuadPart < (LONGLONG)(LAPLACE_MODALITY_NUMBER_PERFCACHE_HEADER_SIZE
                                    + LAPLACE_MODALITY_NUMBER_PERFCACHE_TRAILER_BYTES)) {
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

static void mn_unmap(const uint8_t* base, size_t len) {
    (void)len;
    UnmapViewOfFile((const void*)base);
}

#else

static int mn_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) != 0
        || st.st_size < (off_t)(LAPLACE_MODALITY_NUMBER_PERFCACHE_HEADER_SIZE
                                + LAPLACE_MODALITY_NUMBER_PERFCACHE_TRAILER_BYTES)) {
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

static void mn_unmap(const uint8_t* base, size_t len) {
    munmap((void*)base, len);
}

#endif

void modality_number_table_unload(void) {
    if (g_mn.base) mn_unmap(g_mn.base, g_mn.length);
    memset(&g_mn, 0, sizeof(g_mn));
}

int modality_number_table_is_loaded(void) {
    return g_mn.records != NULL;
}

int modality_number_table_load(const char* path) {
    if (path == NULL) return -1;

    const uint8_t* base = NULL;
    size_t len = 0;
    if (mn_map(path, &base, &len) != 0) return -1;

    const laplace_modality_number_perfcache_header_t* h =
        (const laplace_modality_number_perfcache_header_t*)base;

    if (h->magic != LAPLACE_MODALITY_NUMBER_PERFCACHE_MAGIC
        || h->format_version != LAPLACE_MODALITY_NUMBER_PERFCACHE_VERSION) {
        mn_unmap(base, len);
        return -2;
    }
    if (h->record_size != LAPLACE_MODALITY_NUMBER_PERFCACHE_RECORD_SIZE
        || h->records_offset < LAPLACE_MODALITY_NUMBER_PERFCACHE_HEADER_SIZE) {
        mn_unmap(base, len);
        return -3;
    }

    uint64_t body_end = len - LAPLACE_MODALITY_NUMBER_PERFCACHE_TRAILER_BYTES;
    if (h->records_offset + h->record_count * h->record_size > body_end) {
        mn_unmap(base, len);
        return -3;
    }

    /* v1 denseness: index == value requires exactly 256 contiguous records. */
    if (h->record_count != LAPLACE_MODALITY_NUMBER_PERFCACHE_VALUE_COUNT) {
        mn_unmap(base, len);
        return -3;
    }

    hash128_t crc;
    hash128_blake3(base, (size_t)body_end, &crc);
    const hash128_t* stored = (const hash128_t*)(base + body_end);
    if (memcmp(&crc, stored, sizeof(hash128_t)) != 0) {
        mn_unmap(base, len);
        return -4;
    }

    const laplace_modality_number_perfcache_record_t* recs =
        (const laplace_modality_number_perfcache_record_t*)(base + h->records_offset);
    for (uint64_t i = 0; i < h->record_count; ++i) {
        if (recs[i].value != (uint32_t)i) {
            mn_unmap(base, len);
            return -5;
        }
    }

    modality_number_table_unload();
    g_mn.base = base;
    g_mn.length = len;
    g_mn.header = h;
    g_mn.records = recs;
    g_mn.record_count = h->record_count;
    return 0;
}

const laplace_modality_number_perfcache_record_t*
modality_number_table_lookup(uint32_t value) {
    if (g_mn.records == NULL) return NULL;
    if ((uint64_t)value >= g_mn.record_count) return NULL;
    return &g_mn.records[value];
}

int modality_number_table_lookup_id(uint32_t value, hash128_t* out_id) {
    const laplace_modality_number_perfcache_record_t* r =
        modality_number_table_lookup(value);
    if (r == NULL) return -1;
    if (out_id) *out_id = r->id;
    return 0;
}

int modality_number_table_lookup_geom(uint32_t value,
                                      hash128_t* out_id,
                                      double out_coord[4],
                                      hilbert128_t* out_hb,
                                      uint32_t* out_n,
                                      uint8_t* out_tier) {
    const laplace_modality_number_perfcache_record_t* r =
        modality_number_table_lookup(value);
    if (r == NULL) return -1;
    if (out_id) *out_id = r->id;
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

int modality_number_table_record_count(uint64_t* out_count) {
    if (g_mn.records == NULL) return -1;
    if (out_count) *out_count = g_mn.record_count;
    return 0;
}
