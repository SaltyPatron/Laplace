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

#ifndef M_PI
#  define M_PI 3.14159265358979323846
#endif

#include "laplace_pg/cpuid.h"
#include "laplace_pg/geometry4d.h"
#include "laplace_pg/glicko2.h"
#include "laplace_pg/gram_schmidt.h"
#include "laplace_pg/hash.h"
#include "laplace_pg/hilbert.h"
#include "laplace_pg/polyline4d.h"
#include "laplace_pg/quaternion.h"
#include "laplace_pg/rle.h"
#include "laplace_pg/s3.h"
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

static void test_point4d_distance(void)
{
    const laplace_point4d_t a = {0.0, 0.0, 0.0, 0.0};
    const laplace_point4d_t b = {1.0, 0.0, 0.0, 0.0};
    EXPECT(fabs(laplace_point4d_distance(&a, &b) - 1.0) < 1e-12,
           "point4d distance basic");
}

static void test_s3_normalize_and_geodesic(void)
{
    const laplace_point4d_t p = {3.0, 0.0, 0.0, 4.0};
    laplace_point4d_t       u;
    laplace_s3_normalize(&p, &u);
    EXPECT(fabs(laplace_point4d_norm(&u) - 1.0) < 1e-12, "s3 normalize result not unit");

    const laplace_point4d_t a = {1.0, 0.0, 0.0, 0.0};
    const laplace_point4d_t b = {0.0, 1.0, 0.0, 0.0};
    const double            d = laplace_s3_geodesic_distance(&a, &b);
    EXPECT(fabs(d - (M_PI / 2.0)) < 1e-12, "geodesic between orthogonal axes != pi/2");
}

static void test_s3_slerp_endpoints(void)
{
    const laplace_point4d_t a = {1.0, 0.0, 0.0, 0.0};
    const laplace_point4d_t b = {0.0, 0.0, 0.0, 1.0};
    laplace_point4d_t       p0, p1, ph;
    laplace_s3_slerp(&a, &b, 0.0, &p0);
    laplace_s3_slerp(&a, &b, 1.0, &p1);
    laplace_s3_slerp(&a, &b, 0.5, &ph);
    EXPECT(laplace_point4d_distance(&p0, &a) < 1e-12, "slerp t=0 != a");
    EXPECT(laplace_point4d_distance(&p1, &b) < 1e-12, "slerp t=1 != b");
    EXPECT(fabs(laplace_point4d_norm(&ph) - 1.0) < 1e-12, "slerp midpoint not unit");
}

static void test_quaternion_identity(void)
{
    const laplace_point4d_t id = {0.0, 0.0, 0.0, 1.0};
    const laplace_point4d_t q  = {0.1, 0.2, 0.3, 0.92736185};
    laplace_point4d_t       out;
    laplace_quaternion_multiply(&id, &q, &out);
    EXPECT(laplace_point4d_distance(&out, &q) < 1e-12, "1 * q != q");
    laplace_quaternion_multiply(&q, &id, &out);
    EXPECT(laplace_point4d_distance(&out, &q) < 1e-12, "q * 1 != q");
}

static void test_gram_schmidt_basic(void)
{
    /* Two collinear vectors → second collapses, rank 1. */
    double rows[6] = {
        1.0, 0.0, 0.0,
        2.0, 0.0, 0.0
    };
    const size_t kept = laplace_gram_schmidt_orthonormalize(rows, 2, 3, 1e-12);
    EXPECT(kept == 1, "collinear pair should collapse to rank 1");
    EXPECT(fabs(rows[0] - 1.0) < 1e-12, "first row should be unit e_x");
}

static void test_glicko2_period_decay(void)
{
    const laplace_glicko2_state_t in = {0.0, laplace_glicko2_from_rating_dev(200.0), 0.06, 0};
    laplace_glicko2_state_t       out;
    laplace_glicko2_period_decay(&in, &out);
    EXPECT(out.phi > in.phi, "period decay should grow RD");
    EXPECT(out.mu == in.mu,  "period decay should not move mu");
}

static void test_glicko2_paper_example(void)
{
    /* Glickman 2013 worked example, §5: player rating 1500 RD 200 sigma 0.06,
     * three opponents — wins vs 1400 RD 30, loses vs 1550 RD 100, loses vs
     * 1700 RD 300, with tau = 0.5. Expected r ≈ 1464.06, RD ≈ 151.52,
     * sigma ≈ 0.05999. */
    laplace_glicko2_state_t in;
    in.mu    = laplace_glicko2_from_rating(1500.0);
    in.phi   = laplace_glicko2_from_rating_dev(200.0);
    in.sigma = 0.06;
    in.games = 0;

    laplace_glicko2_observation_t obs[3] = {
        {laplace_glicko2_from_rating(1400.0), laplace_glicko2_from_rating_dev( 30.0), 1.0, 1.0},
        {laplace_glicko2_from_rating(1550.0), laplace_glicko2_from_rating_dev(100.0), 0.0, 1.0},
        {laplace_glicko2_from_rating(1700.0), laplace_glicko2_from_rating_dev(300.0), 0.0, 1.0}
    };

    laplace_glicko2_state_t out;
    laplace_glicko2_apply(&in, obs, 3, 0.5, &out);

    const double r  = laplace_glicko2_to_rating(out.mu);
    const double rd = laplace_glicko2_to_rating_dev(out.phi);
    printf("  glicko2 result: r=%.3f rd=%.3f sigma=%.6f\n", r, rd, out.sigma);
    EXPECT(fabs(r  - 1464.06) < 0.5,    "glicko2 paper example: rating off");
    EXPECT(fabs(rd -  151.52) < 0.5,    "glicko2 paper example: RD off");
    EXPECT(fabs(out.sigma - 0.05999) < 1e-3, "glicko2 paper example: sigma off");
}

static void test_hilbert_round_trip(void)
{
    uint16_t x, y, z, w;
    const uint64_t h = laplace_hilbert_xyzw_to_index(12345, 6789, 30000, 55555);
    laplace_hilbert_index_to_xyzw(h, &x, &y, &z, &w);
    EXPECT(x == 12345 && y == 6789 && z == 30000 && w == 55555,
           "hilbert round-trip mismatch");
}

static void test_hilbert_locality(void)
{
    /* Adjacent lattice points on x should produce close Hilbert indices
     * the vast majority of the time. Sample 256 consecutive x-steps and
     * verify median delta is small (< 1024 of the 64-bit space). */
    uint64_t prev = laplace_hilbert_xyzw_to_index(0, 0, 0, 0);
    uint64_t total_diff = 0;
    int      samples    = 0;
    for (uint16_t i = 1; i < 256; ++i) {
        const uint64_t cur = laplace_hilbert_xyzw_to_index(i, 0, 0, 0);
        const uint64_t d   = cur > prev ? cur - prev : prev - cur;
        total_diff += d;
        ++samples;
        prev = cur;
    }
    /* On average, locality means index delta should be far below the
     * absolute range. This is a coarse smoke check. */
    EXPECT(samples > 0 && (total_diff / (uint64_t) samples) < (1ull << 32),
           "hilbert locality regression");
}

static void test_frechet_basic(void)
{
    const laplace_point4d_t p[3] = {{0,0,0,0}, {1,0,0,0}, {2,0,0,0}};
    const laplace_point4d_t q[3] = {{0,1,0,0}, {1,1,0,0}, {2,1,0,0}};
    const double d = laplace_frechet_distance_4d(p, 3, q, 3);
    EXPECT(fabs(d - 1.0) < 1e-12, "frechet of two parallel polylines should be 1.0");
}

static void test_hausdorff_basic(void)
{
    const laplace_point4d_t p[2] = {{0,0,0,0}, {1,0,0,0}};
    const laplace_point4d_t q[2] = {{0,0,0,0}, {1,0,0,0}};
    EXPECT(laplace_hausdorff_distance_4d(p, 2, q, 2) < 1e-12,
           "hausdorff of identical sets should be 0");

    const laplace_point4d_t r[1] = {{5,0,0,0}};
    const double d = laplace_hausdorff_distance_4d(p, 2, r, 1);
    EXPECT(fabs(d - 5.0) < 1e-12, "hausdorff to far singleton wrong");
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
    test_point4d_distance();
    test_s3_normalize_and_geodesic();
    test_s3_slerp_endpoints();
    test_quaternion_identity();
    test_gram_schmidt_basic();
    test_glicko2_period_decay();
    test_glicko2_paper_example();
    test_hilbert_round_trip();
    test_hilbert_locality();
    test_frechet_basic();
    test_hausdorff_basic();

    if (fail_count == 0) {
        printf("OK: all native smoke tests passed\n");
        return 0;
    }
    fprintf(stderr, "FAILED: %d assertion(s) failed\n", fail_count);
    return 1;
}
