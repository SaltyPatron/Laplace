#include "laplace/core/vocabulary_table.h"

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
    const uint8_t*                                base;
    size_t                                        length;
    const laplace_vocabulary_perfcache_header_t*  header;
    const laplace_vocabulary_perfcache_record_t*  records;
    const uint32_t*                               index;
    const char*                                   strings;
} g_vt = {0};

#ifdef _WIN32

static int vt_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(f, &sz)
        || sz.QuadPart < (LONGLONG)(LAPLACE_VOCABULARY_PERFCACHE_HEADER_SIZE
                                    + LAPLACE_VOCABULARY_PERFCACHE_TRAILER_BYTES)) {
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

static void vt_unmap(const uint8_t* base, size_t len) {
    (void)len;
    UnmapViewOfFile((const void*)base);
}

#else

static int vt_map(const char* path, const uint8_t** out_base, size_t* out_len) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) != 0
        || st.st_size < (off_t)(LAPLACE_VOCABULARY_PERFCACHE_HEADER_SIZE
                                + LAPLACE_VOCABULARY_PERFCACHE_TRAILER_BYTES)) {
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

static void vt_unmap(const uint8_t* base, size_t len) {
    munmap((void*)base, len);
}

#endif

void vocabulary_table_unload(void) {
    if (g_vt.base) vt_unmap(g_vt.base, g_vt.length);
    memset(&g_vt, 0, sizeof(g_vt));
}

int vocabulary_table_is_loaded(void) {
    return g_vt.records != NULL;
}

static int within(uint64_t offset, uint64_t bytes, uint64_t end) {
    return offset <= end && bytes <= end - offset;
}

int vocabulary_table_load(const char* path) {
    if (path == NULL) return -1;
    const uint8_t* base = NULL;
    size_t len = 0;
    if (vt_map(path, &base, &len) != 0) return -1;

    const laplace_vocabulary_perfcache_header_t* h =
        (const laplace_vocabulary_perfcache_header_t*)base;
    if (h->magic != LAPLACE_VOCABULARY_PERFCACHE_MAGIC
        || h->format_version != LAPLACE_VOCABULARY_PERFCACHE_VERSION) {
        vt_unmap(base, len);
        return -2;
    }
    const uint64_t body_end = len - LAPLACE_VOCABULARY_PERFCACHE_TRAILER_BYTES;
    const uint64_t slots = h->index_slots;
    if (h->record_size != LAPLACE_VOCABULARY_PERFCACHE_RECORD_SIZE
        || h->records_offset < LAPLACE_VOCABULARY_PERFCACHE_HEADER_SIZE
        || h->record_count >= LAPLACE_VOCABULARY_INDEX_EMPTY
        || h->record_count > UINT64_MAX / h->record_size
        || !within(h->records_offset, h->record_count * h->record_size, body_end)
        || slots == 0 || (slots & (slots - 1)) != 0 || slots < h->record_count
        || slots > UINT64_MAX / sizeof(uint32_t)
        || !within(h->index_offset, slots * sizeof(uint32_t), body_end)
        || !within(h->strings_offset, h->strings_length, body_end)
        || (h->index_offset % sizeof(uint32_t)) != 0) {
        vt_unmap(base, len);
        return -3;
    }

    hash128_t crc;
    hash128_blake3(base, (size_t)body_end, &crc);
    if (memcmp(&crc, base + body_end, sizeof(hash128_t)) != 0) {
        vt_unmap(base, len);
        return -4;
    }

    const laplace_vocabulary_perfcache_record_t* recs =
        (const laplace_vocabulary_perfcache_record_t*)(base + h->records_offset);
    const char* strings = (const char*)(base + h->strings_offset);
    uint64_t covered = 0;
    for (int f = 0; f < LAPLACE_VOCABULARY_FAMILY_COUNT; ++f) {
        const uint64_t start = h->family_start[f], count = h->family_count[f];
        if (start + count > h->record_count) {
            vt_unmap(base, len);
            return -5;
        }
        for (uint64_t i = 0; i < count; ++i) {
            const laplace_vocabulary_perfcache_record_t* r = &recs[start + i];
            if (r->family != (uint8_t)f || r->code != (uint16_t)(i + 1)
                || (uint64_t)r->label_off + r->label_len >= h->strings_length
                || strings[r->label_off + r->label_len] != '\0') {
                vt_unmap(base, len);
                return -5;
            }
        }
        covered += count;
    }
    if (covered != h->record_count) {
        vt_unmap(base, len);
        return -5;
    }

    vocabulary_table_unload();
    g_vt.base = base;
    g_vt.length = len;
    g_vt.header = h;
    g_vt.records = recs;
    g_vt.index = (const uint32_t*)(base + h->index_offset);
    g_vt.strings = strings;
    return 0;
}

const laplace_vocabulary_perfcache_record_t*
vocabulary_table_lookup(laplace_vocabulary_family_t family, uint16_t code) {
    if (!g_vt.records || (int)family < 0 || family >= LAPLACE_VOCABULARY_FAMILY_COUNT || code == 0)
        return NULL;
    if (code > g_vt.header->family_count[family]) return NULL;
    return &g_vt.records[g_vt.header->family_start[family] + code - 1u];
}

const laplace_vocabulary_perfcache_record_t*
vocabulary_table_find(laplace_vocabulary_family_t family, const hash128_t* id) {
    if (!g_vt.records || !id || (int)family < 0 || family >= LAPLACE_VOCABULARY_FAMILY_COUNT) return NULL;
    const uint64_t mask = g_vt.header->index_slots - 1u;
    for (uint64_t probe = 0, slot = id->lo & mask; probe <= mask; ++probe, slot = (slot + 1u) & mask) {
        const uint32_t i = g_vt.index[slot];
        if (i == LAPLACE_VOCABULARY_INDEX_EMPTY) return NULL;
        if (i < g_vt.header->record_count && g_vt.records[i].family == (uint8_t)family
            && hash128_equals(&g_vt.records[i].id, id))
            return &g_vt.records[i];
    }
    return NULL;
}

const char* vocabulary_table_label(const laplace_vocabulary_perfcache_record_t* record) {
    return record && g_vt.strings ? g_vt.strings + record->label_off : NULL;
}

uint32_t vocabulary_table_family_count(laplace_vocabulary_family_t family) {
    if (!g_vt.records || (int)family < 0 || family >= LAPLACE_VOCABULARY_FAMILY_COUNT) return 0;
    return g_vt.header->family_count[family];
}
