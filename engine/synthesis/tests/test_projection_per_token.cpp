#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <random>
#include <vector>

#include "laplace/synthesis/projection_per_token.h"

#ifdef LAPLACE_HAS_MKL
#  include <oneapi/tbb/global_control.h>
#endif

namespace {

/* float -> BF16 (truncate to upper 16 bits). Exact for the small values used. */
uint16_t f2bf16(float f) {
    uint32_t b;
    std::memcpy(&b, &f, sizeof(b));
    return (uint16_t)(b >> 16);
}

/* Exact BF16 -> f32, matching the kernel + the C# decode. */
float bf16_to_f32(uint16_t bits) {
    const uint32_t u = (uint32_t)bits << 16;
    float f;
    std::memcpy(&f, &u, sizeof(f));
    return f;
}

/* Bit pattern of a double for bitwise-identical comparison. */
uint64_t bits_of(double d) {
    uint64_t u;
    std::memcpy(&u, &d, sizeof(u));
    return u;
}

void neumaier_add(double& sum, double& c, double term) {
    const double t = sum + term;
    if (std::fabs(sum) >= std::fabs(term))
        c += (sum - t) + term;
    else
        c += (term - t) + sum;
    sum = t;
}

/* Trivial serial reference: the identical compensated algorithm the kernel
 * uses (step 1 over o, step 2 over i / uniform fallback). Single-threaded
 * ground truth the parallel kernel must match bit-for-bit. */
std::vector<double> reference(const std::vector<uint16_t>& E, size_t vocab, size_t d_model,
                              const std::vector<uint16_t>& W, size_t out_dim, size_t in_dim) {
    std::vector<double> perInDim(in_dim);
    for (size_t i = 0; i < in_dim; ++i) {
        double sum = 0.0, c = 0.0;
        for (size_t o = 0; o < out_dim; ++o) {
            const double v = (double)bf16_to_f32(W[o * in_dim + i]);
            neumaier_add(sum, c, std::fabs(v));
        }
        perInDim[i] = sum + c;
    }

    std::vector<double> out(vocab);
    if (in_dim == d_model) {
        for (size_t t = 0; t < vocab; ++t) {
            double sum = 0.0, c = 0.0;
            for (size_t i = 0; i < in_dim; ++i) {
                const double e = (double)bf16_to_f32(E[t * d_model + i]);
                neumaier_add(sum, c, e * perInDim[i]);
            }
            out[t] = std::fabs(sum + c);
        }
    } else {
        double sum = 0.0, c = 0.0;
        for (size_t i = 0; i < in_dim; ++i) neumaier_add(sum, c, perInDim[i]);
        const double per_token = (sum + c) / (double)vocab;
        for (size_t t = 0; t < vocab; ++t) out[t] = per_token;
    }
    return out;
}

/* Build a random bf16 tensor with no NaN/Inf patterns (exponent != all ones). */
std::vector<uint16_t> rand_bf16(size_t n, uint32_t seed) {
    std::vector<uint16_t> v(n);
    std::mt19937 rng(seed);
    std::uniform_int_distribution<uint32_t> bitdist(0, 0xFFFFu);
    for (auto& x : v) {
        uint16_t b;
        do { b = (uint16_t)bitdist(rng); } while ((b & 0x7F80u) == 0x7F80u);
        x = b;
    }
    return v;
}

} /* namespace */

/* ── 3a. Known value, projection path (in_dim == d_model). ──────────────────
 * vocab=2, d_model=2, out_dim=2, in_dim=2, all exactly-representable bf16.
 *   W = [[1, -2],
 *        [3,  4]]   -> perInDim = [|1|+|3|, |-2|+|4|] = [4, 6]
 *   E = [[1, 1],    -> out[0] = |1*4 + 1*6| = 10
 *        [2, -1]]   -> out[1] = |2*4 + (-1)*6| = |8 - 6| = 2
 * All sums are exact integers, so the results are exact doubles. */
TEST(ProjectionPerToken, KnownValueProjection2x2) {
    std::vector<uint16_t> W = {
        f2bf16(1.0f), f2bf16(-2.0f),
        f2bf16(3.0f), f2bf16( 4.0f),
    };
    std::vector<uint16_t> E = {
        f2bf16(1.0f), f2bf16( 1.0f),
        f2bf16(2.0f), f2bf16(-1.0f),
    };
    double out[2] = {0, 0};
    ASSERT_EQ(compute_projection_per_token(E.data(), 2, 2, W.data(), 2, 2, out), 0);
    EXPECT_EQ(bits_of(out[0]), bits_of(10.0));
    EXPECT_EQ(bits_of(out[1]), bits_of( 2.0));
}

/* ── 3b. Known value, uniform fallback (in_dim != d_model). ─────────────────
 * vocab=2, d_model=2, but W is out_dim=2 x in_dim=3 (in_dim != d_model).
 *   W = [[1, 2, 3],
 *        [4, 5, 6]] -> perInDim = [5, 7, 9], total = 21
 *   per_token = 21 / vocab = 21 / 2 = 10.5 (exact double), broadcast to all. */
TEST(ProjectionPerToken, KnownValueUniformFallback) {
    std::vector<uint16_t> W = {
        f2bf16(1.0f), f2bf16(2.0f), f2bf16(3.0f),
        f2bf16(4.0f), f2bf16(5.0f), f2bf16(6.0f),
    };
    std::vector<uint16_t> E = {  /* d_model=2, unused on the fallback path */
        f2bf16(1.0f), f2bf16(1.0f),
        f2bf16(1.0f), f2bf16(1.0f),
    };
    double out[2] = {0, 0};
    ASSERT_EQ(compute_projection_per_token(E.data(), 2, 2, W.data(), 2, 3, out), 0);
    EXPECT_EQ(bits_of(out[0]), bits_of(10.5));
    EXPECT_EQ(bits_of(out[1]), bits_of(10.5));
}

TEST(ProjectionPerToken, BadArgsRejected) {
    std::vector<uint16_t> E = {f2bf16(1.0f), f2bf16(2.0f)};
    std::vector<uint16_t> W = {f2bf16(1.0f), f2bf16(2.0f)};
    double out[1];
    EXPECT_EQ(compute_projection_per_token(nullptr, 1, 2, W.data(), 1, 2, out), -1);
    EXPECT_EQ(compute_projection_per_token(E.data(), 1, 2, nullptr, 1, 2, out), -1);
    EXPECT_EQ(compute_projection_per_token(E.data(), 1, 2, W.data(), 1, 2, nullptr), -1);
    EXPECT_EQ(compute_projection_per_token(E.data(), 0, 2, W.data(), 1, 2, out), -1);
    EXPECT_EQ(compute_projection_per_token(E.data(), 1, 0, W.data(), 1, 2, out), -1);
    EXPECT_EQ(compute_projection_per_token(E.data(), 1, 2, W.data(), 0, 2, out), -1);
    EXPECT_EQ(compute_projection_per_token(E.data(), 1, 2, W.data(), 1, 0, out), -1);
}

/* ── 2a. Exactness vs serial f64 reference (projection path), bit-for-bit. ── */
TEST(ProjectionPerToken, BitwiseMatchesSerialReferenceProjection) {
    const size_t vocab = 257, d_model = 193, out_dim = 71;  /* off TBB grain */
    const size_t in_dim = d_model;
    auto E = rand_bf16(vocab * d_model, 0xC0FFEEu);
    auto W = rand_bf16(out_dim * in_dim, 0xDECAFu);

    std::vector<double> out(vocab);
    ASSERT_EQ(compute_projection_per_token(E.data(), vocab, d_model,
                                           W.data(), out_dim, in_dim, out.data()), 0);

    const std::vector<double> ref = reference(E, vocab, d_model, W, out_dim, in_dim);
    for (size_t t = 0; t < vocab; ++t)
        EXPECT_EQ(bits_of(out[t]), bits_of(ref[t])) << "token " << t;
}

/* ── 2b. Exactness vs serial f64 reference (uniform fallback), bit-for-bit. ─ */
TEST(ProjectionPerToken, BitwiseMatchesSerialReferenceFallback) {
    const size_t vocab = 333, d_model = 128, out_dim = 47;
    const size_t in_dim = 211;  /* != d_model -> fallback */
    auto E = rand_bf16(vocab * d_model, 0xABCDEFu);
    auto W = rand_bf16(out_dim * in_dim, 0x123456u);

    std::vector<double> out(vocab);
    ASSERT_EQ(compute_projection_per_token(E.data(), vocab, d_model,
                                           W.data(), out_dim, in_dim, out.data()), 0);

    const std::vector<double> ref = reference(E, vocab, d_model, W, out_dim, in_dim);
    for (size_t t = 0; t < vocab; ++t)
        EXPECT_EQ(bits_of(out[t]), bits_of(ref[t])) << "token " << t;
}

/* ── 1. Determinism: 1 thread vs many threads -> bitwise identical. ────────── */
TEST(ProjectionPerToken, DeterministicAcrossThreadCounts) {
    const size_t vocab = 1024, d_model = 311, out_dim = 97;
    const size_t in_dim = d_model;
    auto E = rand_bf16(vocab * d_model, 0xBADC0DEu);
    auto W = rand_bf16(out_dim * in_dim, 0xFEEDBEEFu);

    std::vector<double> out_many(vocab), out_one(vocab);
    ASSERT_EQ(compute_projection_per_token(E.data(), vocab, d_model,
                                           W.data(), out_dim, in_dim, out_many.data()), 0);

#ifdef LAPLACE_HAS_MKL
    {
        oneapi::tbb::global_control gc(
            oneapi::tbb::global_control::max_allowed_parallelism, 1);
        ASSERT_EQ(compute_projection_per_token(E.data(), vocab, d_model,
                                               W.data(), out_dim, in_dim, out_one.data()), 0);
    }
#else
    ASSERT_EQ(compute_projection_per_token(E.data(), vocab, d_model,
                                           W.data(), out_dim, in_dim, out_one.data()), 0);
#endif

    for (size_t t = 0; t < vocab; ++t)
        EXPECT_EQ(bits_of(out_many[t]), bits_of(out_one[t])) << "token " << t;
}
