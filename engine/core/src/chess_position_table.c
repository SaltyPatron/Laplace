#include "laplace/core/chess_position_table.h"
#include "laplace/core/chess_perfcache_format.h"
#include "laplace/core/hash128.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdatomic.h>
#include <limits.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include <sched.h>
#endif

static struct {
    const uint8_t*                          base;
    size_t                                  length;
    const laplace_chess_perfcache_header_t* header;
    const laplace_chess_perfcache_record_t* records;
    laplace_chess_perfcache_record_t*       filtered_records;
    uint64_t                                record_count;
} g_ch = {0};

#define CH_READER_SHARDS 64u
typedef struct { _Alignas(64) atomic_uint count; } ch_reader_shard_t;
static ch_reader_shard_t g_ch_readers[CH_READER_SHARDS];
static atomic_uint g_ch_next_shard = 0;
static atomic_bool g_ch_publishing = 0;
static atomic_flag g_ch_writer = ATOMIC_FLAG_INIT;
#ifdef _MSC_VER
static __declspec(thread) unsigned g_ch_reader_shard = CH_READER_SHARDS;
#else
static _Thread_local unsigned g_ch_reader_shard = CH_READER_SHARDS;
#endif

static void ch_yield(void) {
#ifdef _WIN32
    SwitchToThread();
#else
    sched_yield();
#endif
}

static void ch_read_enter(void) {
    if (g_ch_reader_shard == CH_READER_SHARDS)
        g_ch_reader_shard = atomic_fetch_add_explicit(&g_ch_next_shard, 1u,
                                                     memory_order_relaxed) % CH_READER_SHARDS;
    atomic_uint* readers = &g_ch_readers[g_ch_reader_shard].count;
    for (;;) {
        while (atomic_load_explicit(&g_ch_publishing, memory_order_seq_cst)) ch_yield();
        unsigned count = atomic_load_explicit(readers, memory_order_relaxed);
        if (count == UINT_MAX) { ch_yield(); continue; }
        if (!atomic_compare_exchange_weak_explicit(readers, &count, count + 1u,
                                                   memory_order_seq_cst, memory_order_relaxed))
            continue;
        if (!atomic_load_explicit(&g_ch_publishing, memory_order_seq_cst)) return;
        atomic_fetch_sub_explicit(readers, 1u, memory_order_seq_cst);
    }
}
static void ch_read_leave(void) {
    atomic_fetch_sub_explicit(&g_ch_readers[g_ch_reader_shard].count, 1u, memory_order_seq_cst);
}
static void ch_write_enter(void) {
    while (atomic_flag_test_and_set_explicit(&g_ch_writer, memory_order_acquire)) ch_yield();
    atomic_store_explicit(&g_ch_publishing, 1, memory_order_seq_cst);
    for (unsigned i = 0; i < CH_READER_SHARDS; ++i)
        while (atomic_load_explicit(&g_ch_readers[i].count, memory_order_seq_cst) != 0) ch_yield();
}
static void ch_write_leave(void) {
    atomic_store_explicit(&g_ch_publishing, 0, memory_order_seq_cst);
    atomic_flag_clear_explicit(&g_ch_writer, memory_order_release);
}

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

static int ch_shard_path(const char* base_path, const char* shard_name, char* out, size_t cap) {
    if (!base_path || !*base_path || !shard_name || !out || cap < 16) return -1;
    size_t len = strlen(base_path);
    const char* ext = (len > 4 && strcmp(base_path + len - 4, ".bin") == 0) ? (base_path + len - 4) : (base_path + len);
    size_t stem_len = (size_t)(ext - base_path);
    int w = snprintf(out, cap, "%.*s.%s.bin", (int)stem_len, base_path, shard_name);
    return (w > 0 && (size_t)w < cap) ? 0 : -1;
}

static int ch_write_shard(const char* out_path,
                          const laplace_chess_perfcache_header_t* src_hdr,
                          const laplace_chess_perfcache_record_t* src_recs,
                          uint64_t src_count,
                          int want_tier1,
                          const char* scope_name) {
    uint64_t count = 0;
    for (uint64_t i = 0; i < src_count; ++i) {
        int is_t1 = src_recs[i].tier <= 1;
        if (is_t1 == want_tier1) count++;
    }
    size_t body_bytes = LAPLACE_CHESS_PERFCACHE_HEADER_SIZE + (size_t)count * LAPLACE_CHESS_PERFCACHE_RECORD_SIZE;
    uint8_t* buf = (uint8_t*)calloc(1, body_bytes + LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES);
    if (!buf) return -1;
    laplace_chess_perfcache_header_t* h = (laplace_chess_perfcache_header_t*)buf;
    *h = *src_hdr;
    h->record_count = count;
    memset(h->scope, 0, sizeof(h->scope));
    snprintf(h->scope, sizeof(h->scope), "%s", scope_name);
    laplace_chess_perfcache_record_t* dst =
        (laplace_chess_perfcache_record_t*)(buf + LAPLACE_CHESS_PERFCACHE_HEADER_SIZE);
    uint64_t k = 0;
    for (uint64_t i = 0; i < src_count; ++i) {
        int is_t1 = src_recs[i].tier <= 1;
        if (is_t1 == want_tier1) dst[k++] = src_recs[i];
    }
    hash128_t crc;
    hash128_blake3(buf, body_bytes, &crc);
    memcpy(buf + body_bytes, &crc, sizeof(crc));

    char tmp[4096];
    snprintf(tmp, sizeof(tmp), "%s.tmp", out_path);
    FILE* f = fopen(tmp, "wb");
    if (!f) { free(buf); return -1; }
    size_t total = body_bytes + LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES;
    int ok = fwrite(buf, 1, total, f) == total;
    if (fclose(f) != 0) ok = 0;
    free(buf);
    if (ok) {
#ifdef _WIN32
        (void)remove(out_path);
#endif
        if (rename(tmp, out_path) != 0) ok = 0;
    }
    if (!ok) { (void)remove(tmp); return -1; }
    return 0;
}

int chess_position_table_split_file(const char* path) {
    if (!path || !*path) return -1;
    const uint8_t* base = NULL;
    size_t len = 0;
    if (ch_map(path, &base, &len) != 0) return -1;
    const laplace_chess_perfcache_header_t* h = (const laplace_chess_perfcache_header_t*)base;
    if (h->magic != LAPLACE_CHESS_PERFCACHE_MAGIC || h->format_version != LAPLACE_CHESS_PERFCACHE_VERSION
        || h->record_size != LAPLACE_CHESS_PERFCACHE_RECORD_SIZE
        || h->records_offset != LAPLACE_CHESS_PERFCACHE_HEADER_SIZE) {
        ch_unmap(base, len);
        return -2;
    }
    uint64_t body_end = len - LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES;
    if ((body_end - h->records_offset) / h->record_size != h->record_count) {
        ch_unmap(base, len);
        return -3;
    }
    const laplace_chess_perfcache_record_t* recs =
        (const laplace_chess_perfcache_record_t*)(base + h->records_offset);
    char t1_path[4096], op_path[4096];
    int rc = 0;
    if (ch_shard_path(path, "tier1", t1_path, sizeof(t1_path)) != 0
        || ch_shard_path(path, "openings", op_path, sizeof(op_path)) != 0
        || ch_write_shard(t1_path, h, recs, h->record_count, 1, "tier1") != 0
        || ch_write_shard(op_path, h, recs, h->record_count, 0, "openings") != 0) {
        rc = -1;
    }
    ch_unmap(base, len);
    return rc;
}

void chess_position_table_unload(void) {
    ch_write_enter();
    if (g_ch.filtered_records) free(g_ch.filtered_records);
    if (g_ch.base) ch_unmap(g_ch.base, g_ch.length);
    memset(&g_ch, 0, sizeof(g_ch));
    ch_write_leave();
}

int chess_position_table_is_loaded(void) {
    ch_read_enter();
    int loaded = g_ch.records != NULL;
    ch_read_leave();
    return loaded;
}

int chess_position_table_load_profile(const char* path, const char* profile) {
    if (path == NULL) return -1;
    if (profile == NULL || profile[0] == '\0')
        profile = getenv("LAPLACE_CHESS_PROFILE");

    int tier1_only = 0;
    if (profile != NULL && profile[0] != '\0') {
        if (strcmp(profile, "tier1") == 0 || strcmp(profile, "vocab") == 0) {
            tier1_only = 1;
        } else if (strcmp(profile, "full") != 0 && strcmp(profile, "all") != 0) {
            return -2;
        }
    }

    const uint8_t* base = NULL;
    size_t len = 0;
    char shard_file[4096];
    int mapped_shard = 0;
    if (tier1_only && ch_shard_path(path, "tier1", shard_file, sizeof(shard_file)) == 0
        && ch_map(shard_file, &base, &len) == 0) {
        mapped_shard = 1;
    } else if (ch_map(path, &base, &len) != 0) {
        return -1;
    }

    const laplace_chess_perfcache_header_t* h =
        (const laplace_chess_perfcache_header_t*)base;

    if (h->magic != LAPLACE_CHESS_PERFCACHE_MAGIC
        || h->format_version != LAPLACE_CHESS_PERFCACHE_VERSION) {
        ch_unmap(base, len);
        return -2;
    }
    if (h->record_size != LAPLACE_CHESS_PERFCACHE_RECORD_SIZE
        || h->records_offset != LAPLACE_CHESS_PERFCACHE_HEADER_SIZE) {
        ch_unmap(base, len);
        return -3;
    }

    uint64_t body_end = len - LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES;
    uint64_t record_bytes = body_end - h->records_offset;
    if (record_bytes % h->record_size != 0
        || h->record_count != record_bytes / h->record_size) {
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

    const laplace_chess_perfcache_record_t* records =
        (const laplace_chess_perfcache_record_t*)(base + h->records_offset);
    for (uint64_t i = 1; i < h->record_count; ++i) {
        if (hash128_compare(&records[i - 1].id, &records[i].id) >= 0) {
            ch_unmap(base, len);
            return -3;
        }
    }

    laplace_chess_perfcache_record_t* filtered = NULL;
    uint64_t active_count = h->record_count;
    if (tier1_only && !mapped_shard) {
        uint64_t n_tier1 = 0;
        for (uint64_t i = 0; i < h->record_count; ++i)
            if (records[i].tier <= 1) n_tier1++;
        filtered = (laplace_chess_perfcache_record_t*)malloc(
            (n_tier1 ? n_tier1 : 1) * sizeof(laplace_chess_perfcache_record_t));
        if (!filtered) {
            ch_unmap(base, len);
            return -1;
        }
        uint64_t dst = 0;
        for (uint64_t i = 0; i < h->record_count; ++i)
            if (records[i].tier <= 1) filtered[dst++] = records[i];
        ch_unmap(base, len);
        base = NULL;
        len = 0;
        h = NULL;
        records = filtered;
        active_count = n_tier1;
    }

    ch_write_enter();
    if (g_ch.filtered_records) free(g_ch.filtered_records);
    if (g_ch.base) ch_unmap(g_ch.base, g_ch.length);
    g_ch.base = base;
    g_ch.length = len;
    g_ch.header = h;
    g_ch.records = records;
    g_ch.filtered_records = filtered;
    g_ch.record_count = active_count;
    ch_write_leave();
    return 0;
}

int chess_position_table_load(const char* path) {
    return chess_position_table_load_profile(path, NULL);
}

static const laplace_chess_perfcache_record_t*
ch_lookup_locked(const hash128_t* id) {
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

#ifdef _MSC_VER
static __declspec(thread) laplace_chess_perfcache_record_t g_ch_copy;
#else
static _Thread_local laplace_chess_perfcache_record_t g_ch_copy;
#endif

const laplace_chess_perfcache_record_t*
chess_position_table_lookup(const hash128_t* id) {
    ch_read_enter();
    const laplace_chess_perfcache_record_t* found = ch_lookup_locked(id);
    int hit = found != NULL;
    if (hit) g_ch_copy = *found;
    ch_read_leave();
    return hit ? &g_ch_copy : NULL;
}

int chess_position_table_lookup_geom(const hash128_t* id,
                                     double out_coord[4],
                                     hilbert128_t* out_hb,
                                     uint32_t* out_n,
                                     uint8_t* out_tier) {
    ch_read_enter();
    const laplace_chess_perfcache_record_t* r = ch_lookup_locked(id);
    if (r == NULL) { ch_read_leave(); return -1; }
    if (out_coord) {
        out_coord[0] = r->coord[0];
        out_coord[1] = r->coord[1];
        out_coord[2] = r->coord[2];
        out_coord[3] = r->coord[3];
    }
    if (out_hb) *out_hb = r->hilbert;
    if (out_n) *out_n = r->n;
    if (out_tier) *out_tier = r->tier;
    ch_read_leave();
    return 0;
}

int chess_position_table_record_count(uint64_t* out_count) {
    ch_read_enter();
    int result = g_ch.records != NULL ? 0 : -1;
    if (out_count) *out_count = result == 0 ? g_ch.record_count : 0;
    ch_read_leave();
    return result;
}
