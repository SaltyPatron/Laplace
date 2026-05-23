#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>

#include "laplace/core/hash128.h"
#include "laplace/core/mantissa.h"

namespace {

uint64_t fp_bits(double d) {
    uint64_t b;
    std::memcpy(&b, &d, sizeof(b));
    return b;
}

uint64_t fp_biased_exp(double d) {
    return (fp_bits(d) >> 52) & 0x7FFULL;
}

uint64_t fp_mantissa(double d) {
    return fp_bits(d) & ((1ULL << 52) - 1ULL);
}

constexpr uint64_t kBiasedExpZero = 0x3FFULL;
constexpr uint64_t kFlagsMask52   = (1ULL << 52) - 1ULL;

}  // namespace

TEST(LaplaceCoreMantissa, ZeroPayloadRoundTrip) {
    const mantissa_payload_t in = {{0, 0}, 0, 0, 0};
    double vertex[4];
    mantissa_pack(vertex, &in);

    mantissa_payload_t out;
    std::memset(&out, 0xAA, sizeof(out));
    mantissa_unpack(vertex, &out);

    EXPECT_EQ(out.entity_id.hi, 0ULL);
    EXPECT_EQ(out.entity_id.lo, 0ULL);
    EXPECT_EQ(out.ordinal, 0u);
    EXPECT_EQ(out.run_length, 0u);
    EXPECT_EQ(out.flags, 0ULL);

    /* All-zero payload → every vertex coord is exactly +1.0. */
    EXPECT_DOUBLE_EQ(vertex[0], 1.0);
    EXPECT_DOUBLE_EQ(vertex[1], 1.0);
    EXPECT_DOUBLE_EQ(vertex[2], 1.0);
    EXPECT_DOUBLE_EQ(vertex[3], 1.0);
}

TEST(LaplaceCoreMantissa, FixedExponentInvariant) {
    /* Every packed vertex coord MUST have biased exponent 0x3FF and magnitude
     * in [1, 2). This is what makes the LINESTRING PG-geometry-valid (finite,
     * normal) under any input payload. */
    const mantissa_payload_t inputs[] = {
        {{0, 0}, 0, 0, 0},
        {{~0ULL, ~0ULL}, 0xFFFFu, 0xFFFFu, kFlagsMask52},
        {{0xDEADBEEFCAFEBABEULL, 0x0123456789ABCDEFULL}, 42, 1024, 0xABCDEFULL},
        {{1, 0}, 1, 1, 1},
    };
    for (const auto& in : inputs) {
        double vertex[4];
        mantissa_pack(vertex, &in);
        for (int i = 0; i < 4; ++i) {
            EXPECT_EQ(fp_biased_exp(vertex[i]), kBiasedExpZero)
                << "vertex[" << i << "] has non-canonical exponent";
            EXPECT_GE(std::abs(vertex[i]), 1.0);
            EXPECT_LT(std::abs(vertex[i]), 2.0);
            EXPECT_TRUE(std::isfinite(vertex[i]));
        }
    }
}

TEST(LaplaceCoreMantissa, FullHashRoundTripsExactly) {
    /* All 128 BLAKE3 bits must survive — no truncation. */
    const hash128_t h = {0xFEDCBA9876543210ULL, 0x0123456789ABCDEFULL};
    const mantissa_payload_t in = {h, 0, 0, 0};

    double vertex[4];
    mantissa_pack(vertex, &in);

    mantissa_payload_t out;
    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.entity_id.hi, h.hi);
    EXPECT_EQ(out.entity_id.lo, h.lo);
}

TEST(LaplaceCoreMantissa, MaxFieldsRoundTrip) {
    const hash128_t h = {~0ULL, ~0ULL};
    const mantissa_payload_t in = {h, 0xFFFFu, 0xFFFFu, kFlagsMask52};

    double vertex[4];
    mantissa_pack(vertex, &in);

    mantissa_payload_t out;
    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.entity_id.hi, h.hi);
    EXPECT_EQ(out.entity_id.lo, h.lo);
    EXPECT_EQ(out.ordinal, 0xFFFFu);
    EXPECT_EQ(out.run_length, 0xFFFFu);
    EXPECT_EQ(out.flags, kFlagsMask52);
}

TEST(LaplaceCoreMantissa, HashLivesInXYZNotM) {
    /* With zero metadata, only XYZ carry data; M is +1.0 (sign 0, mantissa 0). */
    const hash128_t h = {0xDEADBEEFCAFEBABEULL, 0x0123456789ABCDEFULL};
    const mantissa_payload_t in = {h, 0, 0, 0};
    double vertex[4];
    mantissa_pack(vertex, &in);
    EXPECT_DOUBLE_EQ(vertex[3], 1.0) << "M must be free of hash bits";
}

TEST(LaplaceCoreMantissa, MetadataLivesInMNotXYZ) {
    /* With zero hash and zero flags, only M carries data; XYZ are all +1.0. */
    const mantissa_payload_t in = {{0, 0}, 1234u, 7u, 0};
    double vertex[4];
    mantissa_pack(vertex, &in);
    EXPECT_DOUBLE_EQ(vertex[0], 1.0);
    EXPECT_DOUBLE_EQ(vertex[1], 1.0);
    EXPECT_DOUBLE_EQ(vertex[2], 1.0);
    EXPECT_NE(vertex[3], 1.0);

    mantissa_payload_t out;
    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.ordinal, 1234u);
    EXPECT_EQ(out.run_length, 7u);
    EXPECT_EQ(out.flags, 0ULL);
}

TEST(LaplaceCoreMantissa, FlagsSplitAcrossZAndMHighBits) {
    /* Flag bit 0 lands in Z's high half; flag bit 31 (first M-side flag) lands
     * in M's high half. Verifies the documented bit distribution and that
     * round-trip recovers the original 52-bit flag word. */
    const mantissa_payload_t low_flag = {{0, 0}, 0, 0, 1ULL};
    double vertex[4];
    mantissa_pack(vertex, &low_flag);
    EXPECT_NE(fp_mantissa(vertex[2]), 0ULL) << "flags bit 0 must affect Z";
    EXPECT_EQ(fp_mantissa(vertex[3]), 0ULL) << "flags bit 0 must NOT affect M";

    mantissa_payload_t out;
    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.flags, 1ULL);

    const mantissa_payload_t mid_flag = {{0, 0}, 0, 0, 1ULL << 31};
    mantissa_pack(vertex, &mid_flag);
    EXPECT_EQ(fp_mantissa(vertex[2]), 0ULL) << "flags bit 31 must NOT affect Z";
    EXPECT_NE(fp_mantissa(vertex[3]), 0ULL) << "flags bit 31 must affect M";

    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.flags, 1ULL << 31);

    const mantissa_payload_t top_flag = {{0, 0}, 0, 0, 1ULL << 51};
    mantissa_pack(vertex, &top_flag);
    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.flags, 1ULL << 51);
}

TEST(LaplaceCoreMantissa, FlagsAboveBit51AreMasked) {
    /* The high 12 bits of `flags` are documented as MUST-be-zero; if a caller
     * accidentally sets them, they must not leak into other fields. */
    const mantissa_payload_t in = {{0, 0}, 0, 0, ~0ULL};
    double vertex[4];
    mantissa_pack(vertex, &in);

    mantissa_payload_t out;
    mantissa_unpack(vertex, &out);
    EXPECT_EQ(out.flags, kFlagsMask52);
    EXPECT_EQ(out.ordinal, 0u);
    EXPECT_EQ(out.run_length, 0u);
    EXPECT_EQ(out.entity_id.hi, 0ULL);
    EXPECT_EQ(out.entity_id.lo, 0ULL);
}

TEST(LaplaceCoreMantissa, SameHashSameXYZBitsAcrossPayloads) {
    /* The visualization property: same entity_id → identical XYZ bit
     * patterns whenever the Z-side flag bits agree. Same constituent renders
     * to the same 3D scatter position across every vertex that references it.
     *
     * Z holds hash[106..127] + flags[0..30], so XYZ-identity requires both
     * the hash AND the low 31 flag bits to match. M-only metadata (ordinal,
     * run_length, flags[31..51]) is free to vary. */
    const hash128_t h = {0xDEADBEEFCAFEBABEULL, 0x0123456789ABCDEFULL};

    /* M-only flags: bits 31..51 only — leaves Z-side flags[0..30] zero. */
    const uint64_t m_only_flags = (uint64_t)0xABCDE << 31;

    const mantissa_payload_t a = {h, 0,       0,    0};
    const mantissa_payload_t b = {h, 0xFFFFu, 0x7Fu, m_only_flags};
    double va[4], vb[4];
    mantissa_pack(va, &a);
    mantissa_pack(vb, &b);

    EXPECT_EQ(fp_bits(va[0]), fp_bits(vb[0]));
    EXPECT_EQ(fp_bits(va[1]), fp_bits(vb[1]));
    EXPECT_EQ(fp_bits(va[2]), fp_bits(vb[2]));
    EXPECT_NE(fp_bits(va[3]), fp_bits(vb[3]));
}

TEST(LaplaceCoreMantissa, ZShiftsWhenZSideFlagsDiffer) {
    /* Counterpart to SameHashSameXYZBitsAcrossPayloads: Z legitimately differs
     * when flags[0..30] differ, even with identical hash. */
    const hash128_t h = {0xDEADBEEFCAFEBABEULL, 0x0123456789ABCDEFULL};
    const mantissa_payload_t a = {h, 0, 0, 0};
    const mantissa_payload_t b = {h, 0, 0, 0xABCDEFULL};  /* sets bits 0..23 */
    double va[4], vb[4];
    mantissa_pack(va, &a);
    mantissa_pack(vb, &b);

    EXPECT_EQ(fp_bits(va[0]), fp_bits(vb[0]));
    EXPECT_EQ(fp_bits(va[1]), fp_bits(vb[1]));
    EXPECT_NE(fp_bits(va[2]), fp_bits(vb[2]));
    EXPECT_EQ(fp_bits(va[3]), fp_bits(vb[3]));
}

TEST(LaplaceCoreMantissa, DeterministicAcrossRuns) {
    const mantissa_payload_t in = {
        {0xDEADBEEFCAFEBABEULL, 0x0123456789ABCDEFULL},
        42, 1234, 0x123456789ABULL,
    };
    double v1[4], v2[4];
    mantissa_pack(v1, &in);
    mantissa_pack(v2, &in);
    EXPECT_EQ(0, std::memcmp(v1, v2, sizeof(v1)));
}

TEST(LaplaceCoreMantissa, EveryHashBitProbed) {
    /* For each of 128 hash positions, set exactly that one bit, round-trip,
     * verify only that bit comes back. Catches off-by-one in the X/Y/Z
     * splice arithmetic. */
    for (int bit = 0; bit < 128; ++bit) {
        hash128_t h = {0, 0};
        if (bit < 64) h.lo = 1ULL << bit;
        else          h.hi = 1ULL << (bit - 64);
        const mantissa_payload_t in = {h, 0, 0, 0};
        double vertex[4];
        mantissa_pack(vertex, &in);
        mantissa_payload_t out;
        mantissa_unpack(vertex, &out);
        EXPECT_EQ(out.entity_id.lo, h.lo) << "bit " << bit;
        EXPECT_EQ(out.entity_id.hi, h.hi) << "bit " << bit;
        EXPECT_EQ(out.ordinal, 0u) << "bit " << bit << " leaked into ordinal";
        EXPECT_EQ(out.run_length, 0u) << "bit " << bit << " leaked into run_length";
        EXPECT_EQ(out.flags, 0ULL) << "bit " << bit << " leaked into flags";
    }
}

TEST(LaplaceCoreMantissa, EveryFlagBitProbed) {
    for (int bit = 0; bit < 52; ++bit) {
        const mantissa_payload_t in = {{0, 0}, 0, 0, 1ULL << bit};
        double vertex[4];
        mantissa_pack(vertex, &in);
        mantissa_payload_t out;
        mantissa_unpack(vertex, &out);
        EXPECT_EQ(out.flags, 1ULL << bit) << "flag bit " << bit;
        EXPECT_EQ(out.entity_id.lo, 0ULL) << "flag bit " << bit << " leaked into hash";
        EXPECT_EQ(out.entity_id.hi, 0ULL) << "flag bit " << bit << " leaked into hash";
        EXPECT_EQ(out.ordinal, 0u);
        EXPECT_EQ(out.run_length, 0u);
    }
}
