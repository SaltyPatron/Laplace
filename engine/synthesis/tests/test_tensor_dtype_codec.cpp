#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

#include "laplace/synthesis/tensor_dtype_codec.h"

namespace {

uint16_t bits_of_half(float f) {
    /* Reference encoder for round-trip cases only: values chosen below are all
     * exactly representable, so this stays exact. */
    uint32_t u;
    std::memcpy(&u, &f, sizeof u);
    const uint32_t sign = (u >> 16) & 0x8000u;
    int exp = (int)((u >> 23) & 0xFF) - 127 + 15;
    const uint32_t mant = (u >> 13) & 0x03FFu;
    if (f == 0.0f) return (uint16_t)sign;
    return (uint16_t)(sign | ((uint32_t)exp << 10) | mant);
}

float f32_from_bits(uint32_t u) {
    float f;
    std::memcpy(&f, &u, sizeof f);
    return f;
}

}  // namespace

TEST(LaplaceTensorDtypeCodec, NameResolution) {
    EXPECT_EQ(laplace_tensor_dtype_from_name("F32"), LAPLACE_TENSOR_DTYPE_F32);
    EXPECT_EQ(laplace_tensor_dtype_from_name("BF16"), LAPLACE_TENSOR_DTYPE_BF16);
    EXPECT_EQ(laplace_tensor_dtype_from_name("F8_E4M3"), LAPLACE_TENSOR_DTYPE_F8_E4M3);
    EXPECT_EQ(laplace_tensor_dtype_from_name("BOOL"), LAPLACE_TENSOR_DTYPE_BOOL);
    EXPECT_EQ(laplace_tensor_dtype_from_name(nullptr), LAPLACE_TENSOR_DTYPE_UNKNOWN);
}

// Block-quant containers must NOT resolve: ingesting them as zeros would attest
// garbage. The caller is required to refuse.
TEST(LaplaceTensorDtypeCodec, BlockQuantNamesAreUnknown) {
    for (const char* n : {"Q4_K", "Q6_K", "Q8_0", "GPTQ", "AWQ", "", "f32"}) {
        EXPECT_EQ(laplace_tensor_dtype_from_name(n), LAPLACE_TENSOR_DTYPE_UNKNOWN) << n;
    }
    float out[1];
    uint8_t raw[8] = {};
    EXPECT_EQ(laplace_tensor_decode_f32(raw, 1, LAPLACE_TENSOR_DTYPE_UNKNOWN, out), -2);
}

TEST(LaplaceTensorDtypeCodec, ElementSizes) {
    EXPECT_EQ(laplace_tensor_dtype_size(LAPLACE_TENSOR_DTYPE_F64), 8u);
    EXPECT_EQ(laplace_tensor_dtype_size(LAPLACE_TENSOR_DTYPE_F32), 4u);
    EXPECT_EQ(laplace_tensor_dtype_size(LAPLACE_TENSOR_DTYPE_F16), 2u);
    EXPECT_EQ(laplace_tensor_dtype_size(LAPLACE_TENSOR_DTYPE_BF16), 2u);
    EXPECT_EQ(laplace_tensor_dtype_size(LAPLACE_TENSOR_DTYPE_U8), 1u);
    EXPECT_EQ(laplace_tensor_dtype_size(LAPLACE_TENSOR_DTYPE_UNKNOWN), 0u);
}

TEST(LaplaceTensorDtypeCodec, NullArgs) {
    float out[4];
    uint16_t raw[4] = {};
    EXPECT_EQ(laplace_tensor_decode_f32(nullptr, 4, LAPLACE_TENSOR_DTYPE_F16, out), -1);
    EXPECT_EQ(laplace_tensor_decode_f32(raw, 4, LAPLACE_TENSOR_DTYPE_F16, nullptr), -1);
    EXPECT_EQ(laplace_tensor_decode_f32(raw, 0, LAPLACE_TENSOR_DTYPE_F16, out), 0);
}

// n = 19 crosses the 8-wide SIMD body twice and leaves a 3-element tail, so the
// vector path and the scalar remainder must agree bit-for-bit.
TEST(LaplaceTensorDtypeCodec, F16SimdAndTailAgree) {
    const std::vector<float> vals = {0.0f, -0.0f, 1.0f, -1.0f, 0.5f, -0.5f, 2.0f,
                                     -2.0f, 4.0f, 0.25f, 65504.0f, -65504.0f,
                                     3.0f, -3.0f, 0.125f, 8.0f, -8.0f, 16.0f, 1024.0f};
    ASSERT_EQ(vals.size(), 19u);
    std::vector<uint16_t> raw(vals.size());
    for (size_t i = 0; i < vals.size(); ++i) raw[i] = bits_of_half(vals[i]);

    std::vector<float> out(vals.size(), -12345.0f);
    ASSERT_EQ(laplace_tensor_decode_f32(raw.data(), raw.size(),
                                        LAPLACE_TENSOR_DTYPE_F16, out.data()), 0);
    for (size_t i = 0; i < vals.size(); ++i) {
        uint32_t a, b;
        std::memcpy(&a, &out[i], 4);
        std::memcpy(&b, &vals[i], 4);
        EXPECT_EQ(a, b) << "index " << i;
    }
}

TEST(LaplaceTensorDtypeCodec, F16SubnormalsInfAndNaN) {
    // smallest subnormal (2^-24), largest subnormal, +inf, -inf, quiet NaN
    const uint16_t raw[5] = {0x0001, 0x03FF, 0x7C00, 0xFC00, 0x7E00};
    float out[5];
    ASSERT_EQ(laplace_tensor_decode_f32(raw, 5, LAPLACE_TENSOR_DTYPE_F16, out), 0);
    EXPECT_FLOAT_EQ(out[0], std::ldexp(1.0f, -24));
    EXPECT_FLOAT_EQ(out[1], std::ldexp(1023.0f, -24));
    EXPECT_TRUE(std::isinf(out[2]) && out[2] > 0);
    EXPECT_TRUE(std::isinf(out[3]) && out[3] < 0);
    EXPECT_TRUE(std::isnan(out[4]));
}

TEST(LaplaceTensorDtypeCodec, Bf16IsTheHighHalfOfFloat) {
    const std::vector<float> vals = {0.0f, 1.0f, -1.0f, 3.5f, -2.25f, 1e30f,
                                     -1e-30f, 123456.0f, 0.001953125f, 7.0f, -7.0f};
    std::vector<uint16_t> raw(vals.size());
    for (size_t i = 0; i < vals.size(); ++i) {
        uint32_t u;
        std::memcpy(&u, &vals[i], 4);
        raw[i] = (uint16_t)(u >> 16);
    }
    std::vector<float> out(vals.size());
    ASSERT_EQ(laplace_tensor_decode_f32(raw.data(), raw.size(),
                                        LAPLACE_TENSOR_DTYPE_BF16, out.data()), 0);
    for (size_t i = 0; i < vals.size(); ++i) {
        uint32_t got, want;
        std::memcpy(&got, &out[i], 4);
        std::memcpy(&want, &vals[i], 4);
        want &= 0xFFFF0000u;  // bf16 keeps only the high half
        EXPECT_EQ(got, want) << "index " << i;
    }
}

TEST(LaplaceTensorDtypeCodec, F32IsAByteCopy) {
    const float vals[6] = {0.0f, -0.0f, 1.5f, -2.5e-30f, 3.4e38f, 1.0f};
    float out[6];
    ASSERT_EQ(laplace_tensor_decode_f32(vals, 6, LAPLACE_TENSOR_DTYPE_F32, out), 0);
    EXPECT_EQ(std::memcmp(vals, out, sizeof vals), 0);
}

TEST(LaplaceTensorDtypeCodec, F64Narrows) {
    const double vals[3] = {1.0, -0.5, 1e300};
    float out[3];
    ASSERT_EQ(laplace_tensor_decode_f32(vals, 3, LAPLACE_TENSOR_DTYPE_F64, out), 0);
    EXPECT_FLOAT_EQ(out[0], 1.0f);
    EXPECT_FLOAT_EQ(out[1], -0.5f);
    EXPECT_TRUE(std::isinf(out[2]));  // overflows float
}

TEST(LaplaceTensorDtypeCodec, F8E5M2) {
    // sign|eeeee|mm  -> 0x3C = 0 01111 00 = 1.0 ; 0xBC = -1.0 ; 0x00 = 0
    const uint8_t raw[4] = {0x00, 0x3C, 0xBC, 0x40};
    float out[4];
    ASSERT_EQ(laplace_tensor_decode_f32(raw, 4, LAPLACE_TENSOR_DTYPE_F8_E5M2, out), 0);
    EXPECT_FLOAT_EQ(out[0], 0.0f);
    EXPECT_FLOAT_EQ(out[1], 1.0f);
    EXPECT_FLOAT_EQ(out[2], -1.0f);
    EXPECT_FLOAT_EQ(out[3], 2.0f);
}

TEST(LaplaceTensorDtypeCodec, F8E4M3IncludingSubnormalAndNanSlot) {
    // 0x38 = 0 0111 000 -> (1+0)*2^(7-7) = 1.0 ; 0xB8 = -1.0
    // 0x01 = subnormal   -> 1 * 2^-9
    // 0x7F = 0 1111 111  -> the NaN slot
    const uint8_t raw[5] = {0x00, 0x38, 0xB8, 0x01, 0x7F};
    float out[5];
    ASSERT_EQ(laplace_tensor_decode_f32(raw, 5, LAPLACE_TENSOR_DTYPE_F8_E4M3, out), 0);
    EXPECT_FLOAT_EQ(out[0], 0.0f);
    EXPECT_FLOAT_EQ(out[1], 1.0f);
    EXPECT_FLOAT_EQ(out[2], -1.0f);
    EXPECT_FLOAT_EQ(out[3], std::ldexp(1.0f, -9));
    EXPECT_TRUE(std::isnan(out[4]));
}

TEST(LaplaceTensorDtypeCodec, IntegerAndBoolLanes) {
    const int64_t i64[3] = {0, -5, 1 << 20};
    const int32_t i32[3] = {0, -5, 1 << 20};
    const int16_t i16[3] = {0, -5, 4096};
    const int8_t  i8[3]  = {0, -5, 127};
    const uint8_t u8[3]  = {0, 5, 255};
    const uint8_t b[3]   = {0, 1, 200};
    float out[3];

    ASSERT_EQ(laplace_tensor_decode_f32(i64, 3, LAPLACE_TENSOR_DTYPE_I64, out), 0);
    EXPECT_FLOAT_EQ(out[1], -5.0f);
    ASSERT_EQ(laplace_tensor_decode_f32(i32, 3, LAPLACE_TENSOR_DTYPE_I32, out), 0);
    EXPECT_FLOAT_EQ(out[2], 1048576.0f);
    ASSERT_EQ(laplace_tensor_decode_f32(i16, 3, LAPLACE_TENSOR_DTYPE_I16, out), 0);
    EXPECT_FLOAT_EQ(out[2], 4096.0f);
    ASSERT_EQ(laplace_tensor_decode_f32(i8, 3, LAPLACE_TENSOR_DTYPE_I8, out), 0);
    EXPECT_FLOAT_EQ(out[2], 127.0f);
    ASSERT_EQ(laplace_tensor_decode_f32(u8, 3, LAPLACE_TENSOR_DTYPE_U8, out), 0);
    EXPECT_FLOAT_EQ(out[2], 255.0f);
    ASSERT_EQ(laplace_tensor_decode_f32(b, 3, LAPLACE_TENSOR_DTYPE_BOOL, out), 0);
    EXPECT_FLOAT_EQ(out[0], 0.0f);
    EXPECT_FLOAT_EQ(out[1], 1.0f);
    EXPECT_FLOAT_EQ(out[2], 1.0f);  // any non-zero byte is true
}

// Determinism: the same buffer decoded twice must be byte-identical, and length
// must not change the result of the elements a shorter call shares.
TEST(LaplaceTensorDtypeCodec, LengthIndependentAndRepeatable) {
    std::vector<uint16_t> raw(37);
    for (size_t i = 0; i < raw.size(); ++i) raw[i] = (uint16_t)(0x3C00 + i);  // 1.0, 1.0009..
    std::vector<float> full(raw.size()), part(5), again(raw.size());
    ASSERT_EQ(laplace_tensor_decode_f32(raw.data(), raw.size(), LAPLACE_TENSOR_DTYPE_F16, full.data()), 0);
    ASSERT_EQ(laplace_tensor_decode_f32(raw.data(), 5, LAPLACE_TENSOR_DTYPE_F16, part.data()), 0);
    ASSERT_EQ(laplace_tensor_decode_f32(raw.data(), raw.size(), LAPLACE_TENSOR_DTYPE_F16, again.data()), 0);
    EXPECT_EQ(std::memcmp(full.data(), again.data(), full.size() * sizeof(float)), 0);
    EXPECT_EQ(std::memcmp(full.data(), part.data(), part.size() * sizeof(float)), 0);
    EXPECT_FLOAT_EQ(full[0], 1.0f);
    EXPECT_FLOAT_EQ(f32_from_bits(0x3F800000u), 1.0f);
}
