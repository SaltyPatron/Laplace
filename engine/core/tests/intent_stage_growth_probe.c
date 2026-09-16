#include "laplace/core/intent_stage.h"
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#ifndef REPLACEMENT_USES_REALLOC
#error REPLACEMENT_USES_REALLOC must identify the exact compiled owner
#endif

static void check(int condition, const char *message) {
    if (!condition) {
        fprintf(stderr, "intent-stage diagnostic failed: %s\n", message);
        exit(2);
    }
}

/* Linker observation only: every allocation and memcpy still calls libc.
 * These counters cover requested stage payload, not allocator metadata/RSS. */
void *__real_malloc(size_t);
void *__real_calloc(size_t, size_t);
void *__real_realloc(void *, size_t);
void __real_free(void *);
void *__real_memcpy(void *, const void *, size_t);
void *__real___memcpy_chk(void *, const void *, size_t, size_t);

struct allocation { void *pointer; size_t size; };
static struct allocation tracked[8];
static int track_allocations, track_copies;
static size_t live_bytes, live_peak, allocation_calls;
static size_t reservation_peak, grant_limit, reallocations, in_place_growths, moved_growths;
static uint64_t copy_calls, copy_bytes, growth_calls, growth_bytes;
static uint64_t small_copy_sizes[17];
static uint64_t plain_calls, fortified_calls;

static void track(void *pointer, size_t size) {
    check(pointer != NULL, "libc allocation failed");
    for (size_t i = 0; i < sizeof(tracked) / sizeof(tracked[0]); ++i) {
        if (tracked[i].pointer == NULL) {
            tracked[i].pointer = pointer;
            tracked[i].size = size;
            check(size <= SIZE_MAX - live_bytes, "allocation counter overflow");
            live_bytes += size;
            if (live_bytes > live_peak) live_peak = live_bytes;
            if (live_bytes > reservation_peak) reservation_peak = live_bytes;
            check(reservation_peak <= grant_limit, "allocation request exceeded grant");
            ++allocation_calls;
            return;
        }
    }
    check(0, "unexpected concurrent allocation count");
}

void *__wrap_malloc(size_t size) {
    void *pointer = __real_malloc(size);
    if (track_allocations) track(pointer, size);
    return pointer;
}

void *__wrap_calloc(size_t count, size_t width) {
    check(width == 0 || count <= SIZE_MAX / width, "calloc size overflow");
    void *pointer = __real_calloc(count, width);
    if (track_allocations) track(pointer, count * width);
    return pointer;
}

void *__wrap_realloc(void *pointer, size_t size) {
    if (!track_allocations) return __real_realloc(pointer, size);
    check(REPLACEMENT_USES_REALLOC, "baseline bounded owner unexpectedly used realloc");
    const size_t before = live_bytes;
    const uintptr_t prior_address = (uintptr_t)pointer;
    size_t old_size = 0, slot = 8;
    if (pointer != NULL) {
        for (slot = 0; slot < 8; ++slot)
            if (tracked[slot].pointer == pointer) break;
        check(slot < 8, "realloc input is not tracked");
        old_size = tracked[slot].size;
    }
    check(before <= grant_limit && size <= grant_limit - before,
          "realloc old-plus-new reservation exceeded grant");
    void *replacement = __real_realloc(pointer, size);
    check(replacement != NULL, "diagnostic libc realloc failed");
    if (slot < 8) {
        if ((uintptr_t)replacement == prior_address) ++in_place_growths;
        else ++moved_growths;
        live_bytes -= old_size;
        tracked[slot].pointer = NULL;
        tracked[slot].size = 0;
    }
    track(replacement, size);
    if (before + size > reservation_peak) reservation_peak = before + size;
    ++reallocations;
    return replacement;
}

void __wrap_free(void *pointer) {
    if (track_allocations && pointer != NULL) {
        size_t i;
        for (i = 0; i < sizeof(tracked) / sizeof(tracked[0]); ++i) {
            if (tracked[i].pointer == pointer) {
                check(live_bytes >= tracked[i].size, "free counter underflow");
                live_bytes -= tracked[i].size;
                tracked[i].pointer = NULL;
                tracked[i].size = 0;
                break;
            }
        }
        check(i < sizeof(tracked) / sizeof(tracked[0]), "untracked stage allocation freed");
    }
    __real_free(pointer);
}

static void observe_copy(size_t size) {
    if (track_copies) {
        ++copy_calls;
        copy_bytes += size;
        if (size <= 16) ++small_copy_sizes[size];
        if (size > 16) {
            ++growth_calls;
            growth_bytes += size;
        }
    }
}

void *__wrap_memcpy(void *destination, const void *source, size_t size) {
    if (track_copies) ++plain_calls;
    observe_copy(size);
    return __real_memcpy(destination, source, size);
}

/* The retained Ubuntu GCC object lowers the actual growth and field copies to
 * __memcpy_chk. Preserve libc's destination-bound enforcement unchanged. */
void *__wrap___memcpy_chk(void *destination, const void *source,
                         size_t size, size_t destination_size) {
    if (track_copies) ++fortified_calls;
    observe_copy(size);
    return __real___memcpy_chk(destination, source, size, destination_size);
}

static int append_entity(intent_stage_t *stage, size_t ordinal) {
    const hash128_t id = {UINT64_C(0x1111222233334444), (uint64_t)ordinal + 1};
    const hash128_t type = {UINT64_C(0x5555666677778888), UINT64_C(17)};
    return intent_stage_add_entity(stage, &id, (int16_t)(ordinal % 256), &type, NULL);
}

static uint8_t *emit(const intent_stage_t *stage, size_t *size) {
    *size = intent_stage_emit_copy_binary(stage, INTENT_STAGE_TABLE_ENTITIES, NULL, 0);
    uint8_t *bytes = malloc(*size);
    check(bytes != NULL, "COPY comparison allocation failed");
    check(intent_stage_emit_copy_binary(stage, INTENT_STAGE_TABLE_ENTITIES, bytes, *size) == *size,
          "public COPY serializer size changed");
    return bytes;
}

static void write_bytes(const char *directory, const char *name,
                        const uint8_t *bytes, size_t count) {
    char path[1024];
    const int width = snprintf(path, sizeof(path), "%s/%s", directory, name);
    check(width > 0 && (size_t)width < sizeof(path), "diagnostic output path overflow");
    FILE *stream = fopen(path, "wb");
    check(stream != NULL, "cannot open diagnostic byte evidence");
    check(count == 0 || fwrite(bytes, 1, count, stream) == count, "cannot retain exact bytes");
    check(fclose(stream) == 0, "cannot close exact byte evidence");
}

static double monotonic_seconds(void) {
    struct timespec now;
    check(clock_gettime(CLOCK_MONOTONIC, &now) == 0, "monotonic clock unavailable");
    return (double)now.tv_sec + (double)now.tv_nsec / 1000000000.0;
}

static double wall_case(size_t grant, size_t rows) {
    const double start = monotonic_seconds();
    intent_stage_t *stage = intent_stage_new_bounded(0, grant);
    check(stage != NULL, "timed public stage allocation failed");
    for (size_t row = 0; row < rows; ++row)
        check(append_entity(stage, row) == 0, "timed public append failed");
    size_t size;
    uint8_t *copy = emit(stage, &size);
    check(size == 21 + rows * 52 && !intent_stage_allocation_failed(stage),
          "timed public output differs");
    free(copy);
    intent_stage_free(stage);
    return monotonic_seconds() - start;
}

static uint64_t run_case(size_t capacity, const char *directory) {
    intent_stage_t *reference = intent_stage_new(0);
    check(reference != NULL, "unbounded reference stage creation failed");
    const size_t overhead = intent_stage_memory_bytes(reference);
    const size_t payload_grant = 5 * capacity / 2;
    const size_t grant = overhead + payload_grant;
    const size_t rows = (5 * capacity / 4) / 52;
    for (size_t i = 0; i < rows; ++i)
        check(append_entity(reference, i) == 0, "unbounded public append failed");
    size_t reference_size;
    uint8_t *reference_copy = emit(reference, &reference_size);

    live_bytes = live_peak = allocation_calls = 0;
    reservation_peak = reallocations = in_place_growths = moved_growths = 0;
    grant_limit = grant;
    copy_calls = copy_bytes = growth_calls = growth_bytes = 0;
    plain_calls = fortified_calls = 0;
    for (size_t i = 0; i < 17; ++i) small_copy_sizes[i] = 0;
    track_allocations = 1;
    intent_stage_t *bounded = intent_stage_new_bounded(0, grant);
    check(bounded != NULL, "bounded public stage creation failed");
    track_copies = 1;
    for (size_t i = 0; i < rows; ++i) {
        check(append_entity(bounded, i) == 0, "valid append unexpectedly refused");
        check(intent_stage_memory_bytes(bounded) == live_bytes, "live allocation accounting differs");
        check(intent_stage_memory_peak_bytes(bounded) == reservation_peak, "replacement reservation differs");
        check(reservation_peak <= grant, "requested replacement reservation exceeded fixed grant");
    }
    const size_t measured_live = live_bytes, measured_peak = reservation_peak;
    const size_t measured_reallocations = reallocations;
    const size_t measured_in_place = in_place_growths, measured_moved = moved_growths;
    const size_t measured_allocations = allocation_calls;
    const uint64_t measured_growth_calls = growth_calls, measured_growth_bytes = growth_bytes;
    const uint64_t measured_copy_calls = copy_calls, measured_copy_bytes = copy_bytes;
    track_copies = track_allocations = 0;
    check(intent_stage_entity_count(bounded) == rows, "public row count differs");
    check(!intent_stage_allocation_failed(bounded), "successful stage reports failure");
    printf("{\"copy_observation_rows\":%zu,\"total_calls\":%" PRIu64
           ",\"total_bytes\":%" PRIu64 ",\"large_calls\":%" PRIu64
           ",\"large_bytes\":%" PRIu64 ",\"small_sizes\":[",
           rows, measured_copy_calls, measured_copy_bytes, measured_growth_calls, measured_growth_bytes);
    for (size_t i = 0; i < 17; ++i)
        printf("%s%" PRIu64, i ? "," : "", small_copy_sizes[i]);
    puts("]}");
    printf("{\"copy_entry_points\":{\"memcpy\":%" PRIu64
           ",\"__memcpy_chk\":%" PRIu64 "}}\n", plain_calls, fortified_calls);
    check(plain_calls + fortified_calls == measured_copy_calls, "copy entry point accounting differs");
    /* GCC's fortified fixed-width field copies are inline stores. Only actual
     * library calls are counted above; row bytes are checked through the full
     * public COPY output below, rather than inventing calls for inline stores. */

    size_t bounded_size;
    uint8_t *bounded_copy = emit(bounded, &bounded_size);
    check(bounded_size == reference_size && bounded_size == 21 + 52 * rows,
          "public COPY byte length differs");
    check(memcmp(bounded_copy, reference_copy, bounded_size) == 0,
          "bounded and unbounded public COPY bytes differ");
    char filename[128];
    snprintf(filename, sizeof(filename), "case-%zu.copy", capacity);
    write_bytes(directory, filename, bounded_copy, bounded_size);
    if (REPLACEMENT_USES_REALLOC) {
        check(measured_reallocations + 1 == measured_allocations,
              "candidate did not use realloc for every bounded buffer allocation");
        check(measured_growth_calls == 0 && measured_growth_bytes == 0,
              "candidate still performs explicit growth-library copies");
    } else {
        check(measured_growth_calls > rows / 2, "baseline fallback growth was not observed");
        check(measured_growth_bytes > (uint64_t)(52 * rows) * 256,
              "baseline copy amplification was not observed");
    }

    /* Continue only to the nearby grant refusal. The failed stage can contain
     * a partial final row; it is discarded, never represented as valid output. */
    track_allocations = 1;
    size_t attempted = rows;
    int status = 0;
    while (status == 0 && attempted < rows + 8) status = append_entity(bounded, attempted++);
    check(status != 0 && intent_stage_allocation_failed(bounded), "grant boundary did not refuse");
    check(live_bytes == intent_stage_memory_bytes(bounded), "failed-stage live accounting differs");
    check(reservation_peak == intent_stage_memory_peak_bytes(bounded) && reservation_peak <= grant,
          "failure path violated replacement reservation");
    const size_t refusal_peak = reservation_peak;
    const size_t completed_at_refusal = intent_stage_entity_count(bounded);
    track_allocations = 0;
    size_t refused_bytes = 0;
    const uint8_t *refused = intent_stage_tuple_ptr(bounded, INTENT_STAGE_TABLE_ENTITIES, &refused_bytes);
    snprintf(filename, sizeof(filename), "case-%zu.refused-prefix", capacity);
    write_bytes(directory, filename, refused, refused_bytes);
    track_allocations = 1;
    intent_stage_free(bounded);
    check(live_bytes == 0, "bounded stage allocations leaked");
    track_allocations = 0;
    free(bounded_copy);
    free(reference_copy);
    intent_stage_free(reference);

    printf("{\"capacity_anchor_bytes\":%zu,\"fixed_grant_bytes\":%zu,"
           "\"stage_overhead_bytes\":%zu,\"rows\":%zu,\"tuple_bytes\":%zu,"
           "\"copy_bytes\":%zu,\"growth_copy_calls\":%" PRIu64 ","
           "\"growth_copy_bytes\":%" PRIu64 ",\"allocation_calls\":%zu,"
           "\"retained_payload_bytes\":%zu,\"replacement_reservation_peak_bytes\":%zu,"
           "\"refusal_peak_payload_bytes\":%zu,\"copy_equal\":true,"
           "\"grant_refusal_observed\":true,\"stage_freed\":true}\n",
           capacity, grant, overhead, rows, 52 * rows, bounded_size,
           measured_growth_calls, measured_growth_bytes, measured_allocations,
           measured_live, measured_peak, refusal_peak);
    printf("{\"case_allocator\":%zu,\"realloc_calls\":%zu,\"in_place_growths\":%zu,"
           "\"moved_growths\":%zu,\"completed_rows_at_refusal\":%zu,"
           "\"refused_tuple_bytes\":%zu,\"peak_is_conservative_reservation\":true}\n",
           capacity, measured_reallocations, measured_in_place, measured_moved,
           completed_at_refusal, refused_bytes);
    for (size_t trial = 0; trial < 3; ++trial)
        printf("{\"wall_capacity_anchor_bytes\":%zu,\"trial\":%zu,"
               "\"public_create_append_emit_free_seconds\":%.9f,"
               "\"allocation_and_copy_counters_enabled\":false}\n",
               capacity, trial, wall_case(grant, rows));
    return measured_growth_bytes;
}

static int mixed_append(intent_stage_t *stage, size_t ordinal) {
    if (ordinal % 2 == 0) return append_entity(stage, ordinal);
    const hash128_t id = {UINT64_C(37), ordinal + 1};
    const hash128_t subject = {UINT64_C(41), UINT64_C(43)};
    const hash128_t type = {UINT64_C(47), UINT64_C(53)};
    uint8_t mask[32];
    for (size_t i = 0; i < sizeof(mask); ++i) mask[i] = (uint8_t)(i + ordinal);
    return intent_stage_add_attestation(stage, &id, &subject, &type,
        ordinal % 3 == 0 ? &id : NULL, &subject, ordinal % 5 == 0 ? &type : NULL,
        (int16_t)(ordinal % 3), (int64_t)ordinal, 1, 7, 11, 13, mask);
}

static void boundary_cases(const char *directory) {
    const size_t payloads[] = {0, 255, 512, 768, 1024, 1536, 2048, 4096, 8192, 32768};
    for (size_t test = 0; test < sizeof(payloads) / sizeof(payloads[0]); ++test) {
        intent_stage_t *empty = intent_stage_new(0);
        check(empty != NULL, "boundary reference failed");
        const size_t grant = intent_stage_memory_bytes(empty) + payloads[test];
        intent_stage_free(empty);
        intent_stage_t *stage = intent_stage_new_bounded(0, grant);
        intent_stage_t *reference = intent_stage_new(0);
        check(stage != NULL && reference != NULL, "boundary stage failed");
        size_t accepted = 0;
        int status = 0;
        for (size_t operation = 0; operation < 8192; ++operation) {
            check(mixed_append(reference, operation) == 0, "unbounded mixed reference failed");
            status = mixed_append(stage, operation);
            check(intent_stage_memory_peak_bytes(stage) <= grant, "mixed grant exceeded");
            for (int table = 1; table <= 3; ++table) {
                size_t got, want;
                const uint8_t *a = intent_stage_tuple_ptr(stage, (intent_stage_table_t)table, &got);
                const uint8_t *b = intent_stage_tuple_ptr(reference, (intent_stage_table_t)table, &want);
                check(got <= want && (got == 0 || memcmp(a, b, got) == 0),
                      "mixed public tuple prefix differs");
                if (status == 0) check(got == want, "successful mixed row length differs");
            }
            if (status != 0) break;
            ++accepted;
        }
        check(status != 0 && intent_stage_allocation_failed(stage), "mixed grant did not refuse");
        for (int table = 1; table <= 3; ++table) {
            char name[128];
            size_t length;
            const uint8_t *data = intent_stage_tuple_ptr(stage, (intent_stage_table_t)table, &length);
            snprintf(name, sizeof(name), "boundary-%zu-table-%d.bin", test, table);
            write_bytes(directory, name, data, length);
        }
        printf("{\"boundary_case\":%zu,\"fixed_grant_bytes\":%zu,\"accepted_operations\":%zu,"
               "\"status\":%d,\"entities\":%zu,\"physicalities\":%zu,\"attestations\":%zu,"
               "\"retained_bytes\":%zu,\"reserved_peak_bytes\":%zu}\n",
               test, grant, accepted, status, intent_stage_entity_count(stage),
               intent_stage_physicality_count(stage), intent_stage_attestation_count(stage),
               intent_stage_memory_bytes(stage), intent_stage_memory_peak_bytes(stage));
        intent_stage_free(stage);
        intent_stage_free(reference);
    }
}

int main(int argc, char **argv) {
    check(argc == 2, "one fresh output directory is required");
    boundary_cases(argv[1]);
    const size_t capacities[] = {65536, 262144, 1048576};
    for (size_t i = 0; i < sizeof(capacities) / sizeof(capacities[0]); ++i)
        (void)run_case(capacities[i], argv[1]);
    printf("{\"schema\":\"laplace.intent-stage-reallocation-comparison/v1\","
           "\"status\":\"passed\",\"replacement_uses_realloc\":%s,"
           "\"scope\":\"public serializer bytes, grant decisions and wall time\","
           "\"libc_internal_copy_bytes_observed\":false,"
           "\"postgres_ingestion_measured\":false,\"chess_throughput_measured\":false}\n",
           REPLACEMENT_USES_REALLOC ? "true" : "false");
    return 0;
}
