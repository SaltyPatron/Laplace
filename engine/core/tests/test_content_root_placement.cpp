#include <gtest/gtest.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/tier_tree.h"

namespace {

using Tree = std::unique_ptr<tier_tree_t, decltype(&tier_tree_free)>;
using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;

uint32_t be32(const uint8_t* bytes) {
    return (uint32_t(bytes[0]) << 24) | (uint32_t(bytes[1]) << 16)
         | (uint32_t(bytes[2]) << 8) | uint32_t(bytes[3]);
}

struct Field {
    const uint8_t* bytes = nullptr;
    int32_t length = -1;
};

// Decode only the COPY framing in the actual emitted stage. Placement remains
// the production emitter's exact EWKB/Hilbert bytes, not a test-side composer.
bool next_row(const uint8_t* bytes, size_t size, size_t& offset,
              std::vector<Field>& fields) {
    if (offset + 2 > size) return false;
    const uint16_t count = (uint16_t(bytes[offset]) << 8) | bytes[offset + 1];
    offset += 2;
    fields.clear();
    for (uint16_t field = 0; field < count; ++field) {
        if (offset + 4 > size) return false;
        const int32_t length = static_cast<int32_t>(be32(bytes + offset));
        offset += 4;
        if (length < -1 || (length >= 0 && size_t(length) > size - offset)) return false;
        fields.push_back({length < 0 ? nullptr : bytes + offset, length});
        if (length >= 0) offset += size_t(length);
    }
    return true;
}

uint64_t little_bits(const uint8_t* bytes) {
    uint64_t bits = 0;
    for (unsigned i = 0; i < 8; ++i) bits |= uint64_t(bytes[i]) << (8 * i);
    return bits;
}

} // namespace

TEST(LaplaceContentRootPlacement, AtomicSingletonMatchesAuthoritativeFloor) {
    const std::pair<const char*, uint32_t> samples[] = {
        {"a", 0x61}, {" ", 0x20}, {"é", 0xe9}, {"e\xcc\x81", 0xe9}, {"棋", 0x68cb}
    };
    for (const auto& [text, codepoint] : samples) {
        SCOPED_TRACE(text);
        tier_tree_t* raw = nullptr;
        ASSERT_EQ(0, laplace_content_tree_build_public(
            reinterpret_cast<const uint8_t*>(text), std::strlen(text), &raw));
        Tree tree(raw, tier_tree_free);
        tier_node_view_t root{};
        ASSERT_EQ(0, content_witness_tree_root_node(tree.get(), &root));
        hash128_t id{};
        double coord[4]{};
        hilbert128_t hilbert{};
        ASSERT_EQ(0, codepoint_table_resolve_atom(codepoint, &id, coord, &hilbert));
        EXPECT_EQ(0, std::memcmp(&id, &root.id, sizeof(id)));
        EXPECT_EQ(0u, root.tier);
        EXPECT_EQ(0, std::memcmp(coord, root.coord, sizeof(coord)));
        EXPECT_EQ(0, std::memcmp(hilbert.bytes, root.hilbert.bytes, sizeof(hilbert)));
    }
}

TEST(LaplaceContentRootPlacement, ExactTupleMatchesIndependentlyEmittedContent) {
    const char* samples[] = {"dog", "whale song", "café 棋", "cafe\xcc\x81 棋", "q\xcc\x81x", "xक़y"};
    for (const char* text : samples) {
        SCOPED_TRACE(text);
        tier_tree_t* raw = nullptr;
        ASSERT_EQ(0, laplace_content_tree_build_public(
            reinterpret_cast<const uint8_t*>(text), std::strlen(text), &raw));
        Tree tree(raw, tier_tree_free);
        tier_node_view_t root{};
        ASSERT_EQ(0, content_witness_tree_root_node(tree.get(), &root));
        ASSERT_GT(root.tier, 0u);

        Stage stage(intent_stage_new(64), intent_stage_free);
        ASSERT_NE(nullptr, stage);
        hash128_t source{}, emitted_id{};
        hash128_blake3_str("test/native-root-placement", &source);
        ASSERT_EQ(0, content_witness_batch_add(stage.get(),
            reinterpret_cast<const uint8_t*>(text), std::strlen(text), &source, &emitted_id));
        ASSERT_EQ(0, std::memcmp(&root.id, &emitted_id, sizeof(emitted_id)));
        size_t bytes_count = 0;
        const uint8_t* bytes = intent_stage_tuple_ptr(
            stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &bytes_count);
        ASSERT_NE(nullptr, bytes);
        size_t offset = 0;
        int matching_rows = 0;
        std::vector<Field> fields;
        while (offset < bytes_count) {
            ASSERT_TRUE(next_row(bytes, bytes_count, offset, fields));
            ASSERT_GE(fields.size(), 5u);
            ASSERT_EQ(16, fields[1].length);
            if (std::memcmp(fields[1].bytes, &root.id, sizeof(root.id)) != 0) continue;
            ++matching_rows;
            ASSERT_EQ(2, fields[2].length);
            EXPECT_EQ(0, fields[2].bytes[0]);
            EXPECT_EQ(1, fields[2].bytes[1]);
            ASSERT_EQ(37, fields[3].length);
            ASSERT_EQ(1, fields[3].bytes[0]); // canonical little-endian PointZM
            EXPECT_EQ(1, fields[3].bytes[1]);
            EXPECT_EQ(0, fields[3].bytes[2]);
            EXPECT_EQ(0, fields[3].bytes[3]);
            EXPECT_EQ(0xc0, fields[3].bytes[4]);
            for (size_t axis = 0; axis < 4; ++axis) {
                uint64_t native_bits = 0;
                std::memcpy(&native_bits, &root.coord[axis], sizeof(native_bits));
                EXPECT_EQ(native_bits, little_bits(fields[3].bytes + 5 + axis * 8));
            }
            ASSERT_EQ(16, fields[4].length);
            EXPECT_EQ(0, std::memcmp(fields[4].bytes, root.hilbert.bytes, sizeof(root.hilbert)));
        }
        EXPECT_EQ(1, matching_rows);
    }
}

TEST(LaplaceContentRootPlacement, RejectsInvalidTreesAndEmptyComposition) {
    tier_node_view_t root{};
    EXPECT_EQ(-1, content_witness_tree_root_node(nullptr, &root));
    Tree empty(tier_tree_new(1), tier_tree_free);
    ASSERT_NE(nullptr, empty);
    EXPECT_EQ(-1, content_witness_tree_root_node(empty.get(), nullptr));
    EXPECT_EQ(-2, content_witness_tree_root_node(empty.get(), &root));
    tier_tree_t* output = nullptr;
    EXPECT_EQ(-4, laplace_content_tree_build_public(
        reinterpret_cast<const uint8_t*>(""), 0, &output));
    EXPECT_EQ(nullptr, output);
}

TEST(LaplaceContentRootPlacement, MissingFloorCannotProducePlacement) {
    // This core-test process owns its mapped fixture. Always restore it for
    // subsequent tests; never alter the PostgreSQL server's configuration.
    struct RestoreFloor {
        ~RestoreFloor() {
            EXPECT_EQ(0, codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS));
        }
    } restore;
    codepoint_table_unload();
    tier_tree_t* output = nullptr;
    EXPECT_EQ(-3, laplace_content_tree_build_public(
        reinterpret_cast<const uint8_t*>("alias"), 5, &output));
    EXPECT_EQ(nullptr, output);
}
