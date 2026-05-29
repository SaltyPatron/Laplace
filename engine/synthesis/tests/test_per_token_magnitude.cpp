#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <random>
#include <vector>

#include "laplace/synthesis/per_token_magnitude.h"

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

/* Trivial serial reference: identical per-row algorithm the kernel uses —
 * Neumaier-compensated f64 sum of squares in fixed column order, then sqrt.
 * This is the single-threaded ground truth the parallel kernel must match
 * bit-for-bit. */
double ref_row_l2(const uint16_t* row, size_t cols) {
    double sum = 0.0, c = 0.0;
    for (size_t j = 0; j < cols; ++j) {
        const double v = (double)bf16_to_f32(row[j]);
        const double term = v * v;
        const double t = sum + term;
        if (std::fabs(sum) >= std::fabs(term))
            c += (sum - t) + term;
        else
            c += (term - t) + sum;
        sum = t;
    }
    return std::sqrt(sum + c);
}

std::vector<double> reference(const std::vector<uint16_t>& tensor,
                              size_t rows, size_t cols) {
    std::vector<double> out(rows);
    for (size_t r = 0; r < rows; ++r)
        out[r] = ref_row_l2(tensor.data() + r * cols, cols);
    return out;
}

} /* namespace */

/* ── 3. Known value: hand-checked 2x3 tensor of exact bf16 values. ──────────
 * Row 0 = [1, 2, 2]  -> sqrt(1 + 4 + 4)        = 3.0
 * Row 1 = [3, 0, 4]  -> sqrt(9 + 0 + 16)       = 5.0
 * All inputs are exact in bf16 and the sums are exactly representable, so the
 * expected results are exact doubles 3.0 and 5.0. */
TEST(PerTokenMagnitude, KnownValue2x3) {
    std::vector<uint16_t> t = {
        f2bf16(1.0f), f2bf16(2.0f), f2bf16(2.0f),
        f2bf16(3.0f), f2bf16(0.0f), f2bf16(4.0f),
    };
    double out[2] = {0, 0};
    ASSERT_EQ(compute_per_token_l2_magnitude(t.data(), 2, 3, out), 0);
    EXPECT_EQ(bits_of(out[0]), bits_of(3.0));
    EXPECT_EQ(bits_of(out[1]), bits_of(5.0));
}

TEST(PerTokenMagnitude, BadArgsRejected) {
    std::vector<uint16_t> t = {f2bf16(1.0f), f2bf16(2.0f)};
    double out[1];
    EXPECT_EQ(compute_per_token_l2_magnitude(nullptr, 1, 2, out), -1);
    EXPECT_EQ(compute_per_token_l2_magnitude(t.data(), 1, 2, nullptr), -1);
    EXPECT_EQ(compute_per_token_l2_magnitude(t.data(), 0, 2, out), -1);
    EXPECT_EQ(compute_per_token_l2_magnitude(t.data(), 1, 0, out), -1);
}

/* ── 2. Exactness: kernel == trivial serial f64 reference, bit-for-bit. ───── */
TEST(PerTokenMagnitude, BitwiseMatchesSerialReference) {
    const size_t rows = 257, cols = 193;   /* not a multiple of the TBB grain */
    std::vector<uint16_t> t(rows * cols);
    std::mt19937 rng(0xC0FFEEu);
    std::uniform_int_distribution<uint32_t> bitdist(0, 0xFFFFu);
    for (auto& x : t) {
        /* Avoid NaN/Inf bf16 patterns (exponent all ones) for a clean compare. */
        uint16_t b;
        do { b = (uint16_t)bitdist(rng); } while ((b & 0x7F80u) == 0x7F80u);
        x = b;
    }

    std::vector<double> out(rows);
    ASSERT_EQ(compute_per_token_l2_magnitude(t.data(), rows, cols, out.data()), 0);

    const std::vector<double> ref = reference(t, rows, cols);
    for (size_t r = 0; r < rows; ++r)
        EXPECT_EQ(bits_of(out[r]), bits_of(ref[r])) << "row " << r;
}

/* ── 1. Determinism: 1 thread vs many threads -> bitwise identical. ────────── */
TEST(PerTokenMagnitude, DeterministicAcrossThreadCounts) {
    const size_t rows = 1024, cols = 311;
    std::vector<uint16_t> t(rows * cols);
    std::mt19937 rng(0xBADC0DEu);
    std::uniform_int_distribution<uint32_t> bitdist(0, 0xFFFFu);
    for (auto& x : t) {
        uint16_t b;
        do { b = (uint16_t)bitdist(rng); } while ((b & 0x7F80u) == 0x7F80u);
        x = b;
    }

    std::vector<double> out_many(rows), out_one(rows);
    ASSERT_EQ(compute_per_token_l2_magnitude(t.data(), rows, cols, out_many.data()), 0);

#ifdef LAPLACE_HAS_MKL
    {
        oneapi::tbb::global_control gc(
            oneapi::tbb::global_control::max_allowed_parallelism, 1);
        ASSERT_EQ(compute_per_token_l2_magnitude(t.data(), rows, cols, out_one.data()), 0);
    }
#else
    ASSERT_EQ(compute_per_token_l2_magnitude(t.data(), rows, cols, out_one.data()), 0);
#endif

    for (size_t r = 0; r < rows; ++r)
        EXPECT_EQ(bits_of(out_many[r]), bits_of(out_one[r])) << "row " << r;
}
