#include <gtest/gtest.h>

#include <array>
#include <cstring>

#include "laplace/core/hash128.h"

// Preserve the claim-arity protocol after retiring the client-side standing
// writer: absent objects contribute no vertex; an explicit zero ID does.
TEST(StagedLoad, ClaimIdentityUsesOnlyTheDeclaredVertices) {
    std::array<uint8_t, 48> bytes{};
    for (size_t i = 0; i < bytes.size(); ++i) bytes[i] = static_cast<uint8_t>(i);
    std::array<hash128_t, 3> vertices;
    std::memcpy(vertices.data(), bytes.data(), bytes.size());
    const uint8_t binary[16] = {0xe5,0x28,0xe9,0x57,0x98,0x03,0x7d,0xf4,0x10,0x54,0x3d,0x9f,0x31,0xe3,0x96,0xec};
    const uint8_t ternary[16] = {0x5e,0x99,0x02,0x52,0x4b,0x30,0xf4,0xf4,0xcf,0xd5,0xd2,0x18,0x0f,0xd3,0x57,0xbb};
    const uint8_t explicit_zero[16] = {0xbb,0x7f,0xea,0x38,0x58,0xe8,0x78,0x65,0x8d,0xa2,0x3d,0x1e,0xd4,0xbd,0x82,0x36};
    hash128_t actual;
    hash128_merkle(0, vertices.data(), 2, &actual);
    EXPECT_EQ(0, std::memcmp(&actual, binary, sizeof actual));
    hash128_merkle(0, vertices.data(), 3, &actual);
    EXPECT_EQ(0, std::memcmp(&actual, ternary, sizeof actual));
    vertices[2] = {};
    hash128_merkle(0, vertices.data(), 3, &actual);
    EXPECT_EQ(0, std::memcmp(&actual, explicit_zero, sizeof actual));
    EXPECT_NE(0, std::memcmp(&actual, binary, sizeof actual));
}
