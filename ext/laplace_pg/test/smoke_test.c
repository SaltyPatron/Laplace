/*
 * smoke_test.c — Phase 1+2 sample tests.
 *
 * Phase 1: verify laplace_native links and version is non-zero.
 * Phase 2 / Track B (G2 partial): verify the foundational services produce
 * deterministic, expected outputs against published references.
 *
 * Real per-service test files land in test/<service>/ as services come online.
 */

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "laplace_pg/cpuid.h"
#include "laplace_pg/hash.h"
#include "laplace_pg/rle.h"
#include "laplace_pg/superfib.h"

extern uint32_t laplace_native_version(void);

static int fail_count = 0;
#define EXPECT(cond, msg) do { \
    if (!(cond)) { ++fail_count; fprintf(stderr, "FAIL: %s\n", (msg)); } \
} while (0)

static void test_version(void)
{
    EXPECT(laplace_native_version() != 0u, "version is zero");
}

static void test_cpuid(void)
{
    const laplace_cpu_features_t *f = laplace_cpuid_features();
    EXPECT(f != NULL, "cpuid_features returned NULL");
    /* Any modern x86_64 CPU has SSE4.2; if this fails, the test box is too old. */
    EXPECT(f->sse42, "SSE4.2 should be present on any modern dev box");
    printf("  CPU features: avx2=%d avx2_vnni=%d avx512_f=%d sha_ni=%d aes_ni=%d\n",
           f->avx2, f->avx2_vnni, f->avx512_f, f->sha_ni, f->aes_ni);
}

static void test_hash_atom_empty_vs_nonempty(void)
{
    uint8_t h_empty[LAPLACE_HASH_BYTES];
    uint8_t h_one[LAPLACE_HASH_BYTES];
    laplace_hash_atom(NULL, 0, h_empty);
    const uint8_t single = 'a';
    laplace_hash_atom(&single, 1, h_one);
    EXPECT(memcmp(h_empty, h_one, LAPLACE_HASH_BYTES) != 0,
           "empty and single-byte atom hashes must differ");
}

static void test_hash_composition_order_sensitivity(void)
{
    uint8_t children[2 * LAPLACE_HASH_BYTES];
    for (size_t i = 0; i < LAPLACE_HASH_BYTES; ++i) { children[i] = (uint8_t) i; }
    for (size_t i = 0; i < LAPLACE_HASH_BYTES; ++i) { children[LAPLACE_HASH_BYTES + i] = (uint8_t) (255 - i); }
    int32_t counts[2] = {1, 1};

    uint8_t h_ab[LAPLACE_HASH_BYTES];
    uint8_t h_ba[LAPLACE_HASH_BYTES];
    laplace_hash_composition(children, counts, 2, h_ab);

    uint8_t swapped[2 * LAPLACE_HASH_BYTES];
    memcpy(swapped, children + LAPLACE_HASH_BYTES, LAPLACE_HASH_BYTES);
    memcpy(swapped + LAPLACE_HASH_BYTES, children, LAPLACE_HASH_BYTES);
    laplace_hash_composition(swapped, counts, 2, h_ba);

    EXPECT(memcmp(h_ab, h_ba, LAPLACE_HASH_BYTES) != 0,
           "composition hash must be order-sensitive");
}

static void test_rle_round_trip_bytes(void)
{
    const uint8_t  in[]   = {1, 1, 1, 2, 3, 3, 4, 4, 4, 4};
    uint8_t        vals[10];
    int32_t        cnts[10];
    const size_t   runs = laplace_rle_encode_bytes(in, sizeof in, vals, cnts);
    EXPECT(runs == 4, "expected 4 runs");
    EXPECT(vals[0] == 1 && cnts[0] == 3, "run 0 wrong");
    EXPECT(vals[1] == 2 && cnts[1] == 1, "run 1 wrong");
    EXPECT(vals[2] == 3 && cnts[2] == 2, "run 2 wrong");
    EXPECT(vals[3] == 4 && cnts[3] == 4, "run 3 wrong");

    uint8_t out[16];
    const size_t written = laplace_rle_decode_bytes(vals, cnts, runs, out, sizeof out);
    EXPECT(written == sizeof in, "decoded length mismatch");
    EXPECT(memcmp(in, out, sizeof in) == 0, "RLE round-trip mismatch");
}

static void test_superfib_unit_norm(void)
{
    double q[4];
    for (int i = 0; i < 16; ++i) {
        laplace_super_fibonacci_4d(i, 16, q);
        const double n = sqrt(q[0]*q[0] + q[1]*q[1] + q[2]*q[2] + q[3]*q[3]);
        EXPECT(fabs(n - 1.0) < 1e-9, "super-fibonacci output not on unit S3");
    }
}

static void test_superfib_distinct(void)
{
    double a[4], b[4];
    laplace_super_fibonacci_4d(0, 1024, a);
    laplace_super_fibonacci_4d(1, 1024, b);
    const double dx = a[0]-b[0], dy = a[1]-b[1], dz = a[2]-b[2], dw = a[3]-b[3];
    EXPECT(dx*dx + dy*dy + dz*dz + dw*dw > 1e-12,
           "consecutive super-fibonacci samples must differ");
}

int main(void)
{
    test_version();
    test_cpuid();
    test_hash_atom_empty_vs_nonempty();
    test_hash_composition_order_sensitivity();
    test_rle_round_trip_bytes();
    test_superfib_unit_norm();
    test_superfib_distinct();

    if (fail_count == 0) {
        printf("OK: all native smoke tests passed\n");
        return 0;
    }
    fprintf(stderr, "FAILED: %d assertion(s) failed\n", fail_count);
    return 1;
}
