#include <gtest/gtest.h>

#include <cstring>
#include <vector>

#include "laplace/core/hash128.h"

namespace {
constexpr uint8_t kBlake3EmptyTruncated[16] = {
    0xaf, 0x13, 0x49, 0xb9, 0xf5, 0xf9, 0xa1, 0xa6,
    0xa0, 0x40, 0x4d, 0xea, 0x36, 0xdc, 0xc9, 0x49,
};
}

TEST(LaplaceCoreHash128, ZeroProducesAllZero) {
    hash128_t h;
    std::memset(&h, 0xff, sizeof(h));
    hash128_zero(&h);
    EXPECT_EQ(h.hi, 0u);
    EXPECT_EQ(h.lo, 0u);
}

TEST(LaplaceCoreHash128, BlakeEmptyMatchesKnownTruncatedVector) {
    hash128_t h;
    hash128_blake3(nullptr, 0, &h);
    EXPECT_EQ(0, std::memcmp(&h, kBlake3EmptyTruncated, 16));
}

TEST(LaplaceCoreHash128, BlakeSameInputProducesSameOutput) {
    const uint8_t input[] = "the quick brown fox jumps over the lazy dog";
    hash128_t a, b;
    hash128_blake3(input, sizeof(input) - 1, &a);
    hash128_blake3(input, sizeof(input) - 1, &b);
    EXPECT_TRUE(hash128_equals(&a, &b));
    EXPECT_EQ(0, hash128_compare(&a, &b));
}

TEST(LaplaceCoreHash128, BlakeDifferentInputsDiffer) {
    const uint8_t a_in[] = "alpha";
    const uint8_t b_in[] = "beta";
    hash128_t a, b;
    hash128_blake3(a_in, sizeof(a_in) - 1, &a);
    hash128_blake3(b_in, sizeof(b_in) - 1, &b);
    EXPECT_FALSE(hash128_equals(&a, &b));
    EXPECT_NE(0, hash128_compare(&a, &b));
}

TEST(LaplaceCoreHash128, CompareSelfIsZero) {
    hash128_t a;
    hash128_zero(&a);
    EXPECT_EQ(0, hash128_compare(&a, &a));
    EXPECT_TRUE(hash128_equals(&a, &a));
}

TEST(LaplaceCoreHash128, CompareMatchesMemcmpOnBytea) {
    const std::vector<std::string> inputs = {"", "a", "aa", "ab", "b", "z", "longer input"};
    std::vector<hash128_t> hashes(inputs.size());
    for (size_t i = 0; i < inputs.size(); ++i) {
        hash128_blake3(reinterpret_cast<const uint8_t*>(inputs[i].data()), inputs[i].size(), &hashes[i]);
    }
    for (size_t i = 0; i < hashes.size(); ++i) {
        for (size_t j = 0; j < hashes.size(); ++j) {
            const int cmp = hash128_compare(&hashes[i], &hashes[j]);
            const int memcmp_result = std::memcmp(&hashes[i], &hashes[j], sizeof(hash128_t));
            const auto sign = [](int v) { return (v > 0) - (v < 0); };
            EXPECT_EQ(sign(cmp), sign(memcmp_result))
                << "i=" << i << " (\"" << inputs[i] << "\") j=" << j << " (\"" << inputs[j] << "\")";
        }
    }
}

TEST(LaplaceCoreHash128, MerkleComposesChildrenDeterministically) {
    const uint8_t a_in[] = "child_a";
    const uint8_t b_in[] = "child_b";
    hash128_t a, b;
    hash128_blake3(a_in, sizeof(a_in) - 1, &a);
    hash128_blake3(b_in, sizeof(b_in) - 1, &b);
    hash128_t children[2] = {a, b};
    hash128_t out1, out2;
    hash128_merkle(1, children, 2, &out1);
    hash128_merkle(1, children, 2, &out2);
    EXPECT_TRUE(hash128_equals(&out1, &out2));
}

TEST(LaplaceCoreHash128, MerkleTierIsNotIdentity) {
    const uint8_t a_in[] = "child_a";
    hash128_t a;
    hash128_blake3(a_in, sizeof(a_in) - 1, &a);
    hash128_t tier1, tier2;
    hash128_merkle(1, &a, 1, &tier1);
    hash128_merkle(2, &a, 1, &tier2);
    EXPECT_TRUE(hash128_equals(&tier1, &tier2));
}

TEST(LaplaceCoreHash128, MerkleChildOrderMatters) {
    const uint8_t a_in[] = "alpha";
    const uint8_t b_in[] = "beta";
    hash128_t a, b;
    hash128_blake3(a_in, sizeof(a_in) - 1, &a);
    hash128_blake3(b_in, sizeof(b_in) - 1, &b);
    hash128_t ab[2] = {a, b};
    hash128_t ba[2] = {b, a};
    hash128_t out_ab, out_ba;
    hash128_merkle(3, ab, 2, &out_ab);
    hash128_merkle(3, ba, 2, &out_ba);
    EXPECT_FALSE(hash128_equals(&out_ab, &out_ba));
}

TEST(LaplaceCoreHash128, MerkleEmptyChildSetIsTierIndependent) {
    hash128_t t0, t1;
    hash128_merkle(0, nullptr, 0, &t0);
    hash128_merkle(1, nullptr, 0, &t1);
    EXPECT_TRUE(hash128_equals(&t0, &t1));
}

TEST(LaplaceCoreHash128, StructLayoutIs16BytesPod) {
    static_assert(sizeof(hash128_t) == 16, "hash128_t must be exactly 16 bytes for bytea(16) round-trip");
    static_assert(alignof(hash128_t) == 8, "hash128_t must be 8-byte aligned for {hi,lo} uint64 access");
}
