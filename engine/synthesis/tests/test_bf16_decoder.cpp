#include <gtest/gtest.h>

#include <cmath>
#include <cstdint>
#include <cstring>

#include "laplace/synthesis/bf16_decoder.h"

static uint16_t f32_to_bf16(float f) {
    uint32_t u;
    std::memcpy(&u, &f, sizeof(u));
    return (uint16_t)(u >> 16);
}

TEST(LaplaceSynthesisBf16Decoder, NullReturnsMinusOne) {
    double out[4];
    EXPECT_EQ(laplace_bf16_decode(nullptr, 4, out), -1);
    uint16_t data[4] = {};
    EXPECT_EQ(laplace_bf16_decode(data, 4, nullptr), -1);
}

TEST(LaplaceSynthesisBf16Decoder, ZeroElements) {
    uint16_t data[1] = {0};
    double out[1] = {99.0};
    EXPECT_EQ(laplace_bf16_decode(data, 0, out), 0);
    EXPECT_DOUBLE_EQ(out[0], 99.0);
}

TEST(LaplaceSynthesisBf16Decoder, PositiveOne) {
    float f = 1.0f;
    uint16_t bf16 = f32_to_bf16(f);
    double out = 0.0;
    EXPECT_EQ(laplace_bf16_decode(&bf16, 1, &out), 0);
    EXPECT_NEAR(out, 1.0, 1e-2);
}

TEST(LaplaceSynthesisBf16Decoder, NegativeValue) {
    float f = -3.14f;
    uint16_t bf16 = f32_to_bf16(f);
    double out = 0.0;
    EXPECT_EQ(laplace_bf16_decode(&bf16, 1, &out), 0);
    EXPECT_NEAR(out, -3.14, 0.02);
}

TEST(LaplaceSynthesisBf16Decoder, Zero) {
    uint16_t bf16 = 0x0000;
    double out = 99.0;
    EXPECT_EQ(laplace_bf16_decode(&bf16, 1, &out), 0);
    EXPECT_DOUBLE_EQ(out, 0.0);
}

TEST(LaplaceSynthesisBf16Decoder, BatchOf8Aligned) {
    float inputs[8] = {1.0f, -1.0f, 0.5f, -0.5f, 2.0f, -2.0f, 0.0f, 100.0f};
    uint16_t bf16[8];
    for (int i = 0; i < 8; ++i) bf16[i] = f32_to_bf16(inputs[i]);

    double out[8];
    EXPECT_EQ(laplace_bf16_decode(bf16, 8, out), 0);
    for (int i = 0; i < 8; ++i) {
        EXPECT_NEAR(out[i], (double)inputs[i], std::fabs((double)inputs[i]) * 0.01 + 0.01)
            << "at index " << i;
    }
}

TEST(LaplaceSynthesisBf16Decoder, BatchOf17WithScalarTail) {
    float inputs[17];
    uint16_t bf16[17];
    for (int i = 0; i < 17; ++i) {
        inputs[i] = (float)(i - 8) * 0.25f;
        bf16[i] = f32_to_bf16(inputs[i]);
    }
    double out[17];
    EXPECT_EQ(laplace_bf16_decode(bf16, 17, out), 0);
    for (int i = 0; i < 17; ++i) {
        EXPECT_NEAR(out[i], (double)inputs[i],
                    std::fabs((double)inputs[i]) * 0.01 + 0.01)
            << "at index " << i;
    }
}

TEST(LaplaceSynthesisBf16Decoder, SpecialValues) {
    uint16_t pos_inf = 0x7F80;
    uint16_t neg_inf = 0xFF80;
    uint16_t nan_val = 0x7FC0;

    double out[3];
    uint16_t data[3] = {pos_inf, neg_inf, nan_val};
    EXPECT_EQ(laplace_bf16_decode(data, 3, out), 0);

    EXPECT_TRUE(std::isinf(out[0]) && out[0] > 0);
    EXPECT_TRUE(std::isinf(out[1]) && out[1] < 0);
    EXPECT_TRUE(std::isnan(out[2]));
}
