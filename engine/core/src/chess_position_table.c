#include "laplace/core/chess_position_table.h"
#include "laplace/core/chess_perfcache_format.h"
#include "laplace/core/hash128.h"

#include <stddef.h>
#include <stdint.h>
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
    uint64_t                                record_count;
} g_ch = {0};

/* Immutable map readers remain concurrent. A fixed set of cache-line-separated
 * reader counters avoids making every search contend on one shared counter.
 * Threads select a shard once; reuse after 64 threads remains safe because each
 * shard counts its current readers. This is bounded state, not an unbounded TLS
 * registration/retirement list. Publication alone drains all shards. */
#define CH_READER_SHARDS 64u
typedef struct { _Alignas(64) atomic_uint count; } ch_reader_shard_t;
static ch_reader_shard_t g_ch_readers[CH_READER_SHARDS];
static atomic_uint g_ch_next_shard = ATOMIC_VAR_INIT(0);
static atomic_bool g_ch_publishing = ATOMIC_VAR_INIT(0);
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
        /* SC ordering is intentional: either publication sees this reader count,
         * or this recheck sees publication before any mapped bytes are touched. */
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

void chess_position_table_unload(void) {
    ch_write_enter();
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
        || h->records_offset != LAPLACE_CHESS_PERFCACHE_HEADER_SIZE) {
        ch_unmap(base, len);
        return -3;
    }

    uint64_t body_end = len - LAPLACE_CHESS_PERFCACHE_TRAILER_BYTES;
    /* Divide the actual mapped byte envelope before trusting the stored count.
     * Multiplication/addition of an untrusted count can wrap and admit a table
     * whose binary search walks outside its mapping. Version 1 has no padding
     * or extra sections after the records. */
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

    ch_write_enter();
    if (g_ch.base) ch_unmap(g_ch.base, g_ch.length);
    g_ch.base = base;
    g_ch.length = len;
    g_ch.header = h;
    g_ch.records = records;
    g_ch.record_count = h->record_count;
    ch_write_leave();
    return 0;
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
