#include <gtest/gtest.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/physicality_descriptor_admission.h"
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

void expect_atomic_body(const std::vector<Field>& fields,
                        const codepoint_entry_t& floor, int64_t observed_at) {
    ASSERT_EQ(fields.size(), 10u);
    hash128_t placement{};
    laplace_physicality_id_compute(floor.hash, 1, &placement);
    ASSERT_EQ(fields[0].length, 16);
    EXPECT_EQ(std::memcmp(fields[0].bytes, &placement, 16), 0);
    ASSERT_EQ(fields[1].length, 16);
    EXPECT_EQ(std::memcmp(fields[1].bytes, &floor.hash, 16), 0);
    ASSERT_EQ(fields[2].length, 2);
    EXPECT_EQ(fields[2].bytes[0], 0);
    EXPECT_EQ(fields[2].bytes[1], 1);
    ASSERT_EQ(fields[3].length, 37);
    const uint8_t point_header[] = {1, 1, 0, 0, 0xc0};
    EXPECT_EQ(std::memcmp(fields[3].bytes, point_header, sizeof(point_header)), 0);
    for (size_t axis = 0; axis < 4; ++axis) {
        uint64_t bits = 0;
        std::memcpy(&bits, &floor.coord[axis], sizeof(bits));
        EXPECT_EQ(little_bits(fields[3].bytes + 5 + axis * 8), bits);
    }
    ASSERT_EQ(fields[4].length, 16);
    EXPECT_EQ(std::memcmp(fields[4].bytes, &floor.hilbert, 16), 0);
    EXPECT_EQ(fields[5].length, -1); // No invented self trajectory.
    ASSERT_EQ(fields[6].length, 4);
    EXPECT_EQ(be32(fields[6].bytes), 0u);
    EXPECT_EQ(fields[7].length, -1);
    EXPECT_EQ(fields[8].length, -1);
    ASSERT_EQ(fields[9].length, 8);
    uint64_t bits = 0;
    for (size_t i = 0; i < 8; ++i) bits = (bits << 8u) | fields[9].bytes[i];
    int64_t pg_time = 0;
    std::memcpy(&pg_time, &bits, sizeof(pg_time));
    EXPECT_EQ(pg_time + INTENT_STAGE_PG_EPOCH_UNIX_US, observed_at);
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

namespace {

int alternate_floor(uint32_t atom, void*, hash128_t* id, double coord[4], hilbert128_t* hb) {
    const int rc = codepoint_table_resolve_atom(atom, id, coord, hb);
    if (rc != 0) return rc;
    // A second supplied geometry provider uses the same canonical atom IDs.
    // Recomposition remains owned by the actual hash/geometry composer.
    coord[0] *= 0.5;
    coord[1] *= 0.5;
    hilbert4d_encode(coord, hb);
    return 0;
}

} // namespace

TEST(LaplaceContentObservations, ExistingRootRetainsSourcesAndFormsWithoutDuplicateEntities) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    constexpr size_t budget = 64u * 1024u * 1024u;
    const hash128_t source_a{101, 102}, source_b{201, 202}, unit_a{301, 302}, unit_b{401, 402};
    tier_tree_t* raw_tree = nullptr;
    ASSERT_EQ(0, content_witness_tree_build(reinterpret_cast<const uint8_t*>("ab"), 2, &raw_tree));
    Tree tree(raw_tree, tier_tree_free);
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(nullptr, stage);
    hash128_t original{}, repeated{}, alternate{};
    ASSERT_EQ(0, content_witness_emit_tree(stage.get(), tree.get(), &source_a, nullptr, 0, &original));
    ASSERT_EQ(1u, intent_stage_physicality_count(stage.get()));
    ASSERT_EQ(0, content_witness_emit_tree(stage.get(), tree.get(), &source_b, nullptr, 0, &repeated));
    ASSERT_EQ(2u, intent_stage_physicality_count(stage.get()));
    ASSERT_EQ(0, hash_composer_run(tree.get(), alternate_floor, nullptr));
    ASSERT_EQ(0, content_witness_emit_tree(stage.get(), tree.get(), &source_b, nullptr, 0, &alternate));
    ASSERT_EQ(0, content_witness_emit_tree(stage.get(), tree.get(), &source_b, nullptr, 0, &repeated));
    EXPECT_TRUE(hash128_equals(&original, &alternate));
    EXPECT_TRUE(hash128_equals(&original, &repeated));
    EXPECT_EQ(1u, intent_stage_entity_count(stage.get()));
    ASSERT_EQ(4u, intent_stage_physicality_count(stage.get()));

    size_t size = 0, offset = 0;
    const uint8_t* entity_bytes = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ENTITIES, &size);
    std::vector<Field> fields;
    ASSERT_TRUE(next_row(entity_bytes, size, offset, fields));
    // An entity row is its identity alone (id, tier, type): sources witness it through
    // attestations, never through a column on the entity.
    ASSERT_EQ(3u, fields.size());
    ASSERT_EQ(16, fields[0].length);
    EXPECT_EQ(0, std::memcmp(fields[0].bytes, &original, 16));
    EXPECT_EQ(size, offset);

    physicality_descriptor_vocabulary_t* raw_vocabulary = nullptr;
    ASSERT_EQ(PHYSICALITY_DESCRIPTOR_OK,
        physicality_descriptor_vocabulary_create(&source_a, budget, &raw_vocabulary));
    std::unique_ptr<physicality_descriptor_vocabulary_t, decltype(&physicality_descriptor_vocabulary_free)>
        vocabulary(raw_vocabulary, physicality_descriptor_vocabulary_free);
    const intent_stage_t* source_stage = stage.get();
    const physicality_descriptor_limits_t limits{budget};
    physicality_descriptor_capture_t* raw_capture = nullptr;
    ASSERT_EQ(PHYSICALITY_DESCRIPTOR_OK, physicality_descriptor_capture_stages(&source_stage, 1,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits, budget, &raw_capture));
    std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>
        capture(raw_capture, physicality_descriptor_capture_free);
    size_t count = 0;
    const hash128_t* descriptors = physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(capture.get()), &count);
    ASSERT_EQ(4u, count);
    EXPECT_TRUE(hash128_equals(&descriptors[0], &descriptors[1]));
    EXPECT_TRUE(hash128_equals(&descriptors[2], &descriptors[3]));
    EXPECT_FALSE(hash128_equals(&descriptors[0], &descriptors[2]));

    const physicality_descriptor_source_observation_t a{source_a, unit_a, 0.8}, b{source_b, unit_b, 0.9};
    const std::array<physicality_descriptor_source_observation_t, 4> sources{a, b, b, b};
    physicality_descriptor_materialization_t* raw_materialized = nullptr;
    ASSERT_EQ(PHYSICALITY_DESCRIPTOR_OK, physicality_descriptor_materialize(capture.get(), vocabulary.get(),
        nullptr, 0, nullptr, 0, nullptr, 0, sources.data(), sources.size(),
        INTENT_STAGE_PG_EPOCH_UNIX_US, budget, &raw_materialized));
    std::unique_ptr<physicality_descriptor_materialization_t, decltype(&physicality_descriptor_materialization_free)>
        materialized(raw_materialized, physicality_descriptor_materialization_free);
    size_t provenance_count = 0;
    const auto* provenance =
        physicality_descriptor_materialization_observations(materialized.get(), &provenance_count);
    ASSERT_NE(provenance, nullptr);
    ASSERT_EQ(provenance_count, sources.size());
    for (size_t i = 0; i < provenance_count; ++i) {
        EXPECT_TRUE(hash128_equals(&provenance[i].entity_id, &original));
        EXPECT_TRUE(hash128_equals(&provenance[i].descriptor_id, &descriptors[i]));
        EXPECT_TRUE(hash128_equals(&provenance[i].source_id, &sources[i].source_id));
        EXPECT_TRUE(hash128_equals(&provenance[i].source_unit_id, &sources[i].source_unit_id));
    }
    Stage generated(physicality_descriptor_materialization_take_stage(materialized.get()), intent_stage_free);
    ASSERT_NE(nullptr, generated);
    EXPECT_EQ(0u, intent_stage_attestation_count(generated.get()));
}

TEST(LaplaceContentObservations, PresentRootDoesNotSuppressMissingDescendantsOrOccurrenceForms) {
    tier_tree_t* raw_tree = nullptr;
    ASSERT_EQ(0, content_witness_tree_build(reinterpret_cast<const uint8_t*>("ab ab"), 5, &raw_tree));
    Tree tree(raw_tree, tier_tree_free);
    tier_node_view_t expected_root{};
    ASSERT_EQ(0, content_witness_tree_root_node(tree.get(), &expected_root));
    tier_tree_t* raw_word = nullptr;
    ASSERT_EQ(0, content_witness_tree_build(reinterpret_cast<const uint8_t*>("ab"), 2, &raw_word));
    Tree word(raw_word, tier_tree_free);
    tier_node_view_t expected_word{};
    ASSERT_EQ(0, content_witness_tree_root_node(word.get(), &expected_word));
    ASSERT_FALSE(hash128_equals(&expected_root.id, &expected_word.id));
    const size_t nodes = tier_tree_node_count(tree.get());
    const hash128_t source{101, 102};

    for (const bool all_present : {false, true}) {
        SCOPED_TRACE(all_present);
        std::vector<uint8_t> bitmap((nodes + 7u) / 8u, 0);
        for (size_t index = 0; index < nodes; ++index) {
            tier_node_view_t node{};
            ASSERT_EQ(0, tier_tree_get_node(tree.get(), static_cast<uint32_t>(index), &node));
            // Identity is tier-blind: mark every occurrence of the known root,
            // including collapsed singleton wrappers with that same identity.
            if (all_present || hash128_equals(&node.id, &expected_root.id))
                bitmap[index / 8u] |= uint8_t(1u << (index & 7u));
        }
        hash128_t root{}, replay{};
        Stage stage(intent_stage_new(0), intent_stage_free);
        ASSERT_NE(nullptr, stage);
        ASSERT_EQ(0, content_witness_emit_tree(stage.get(), tree.get(), &source, bitmap.data(), nodes, &root));
        EXPECT_TRUE(hash128_equals(&root, &expected_root.id));
        EXPECT_EQ(all_present ? 0u : 1u, intent_stage_entity_count(stage.get()));
        // The two "ab" occurrences collapse to one placement by the identity
        // law (same content, same form), so exactly one word form plus the
        // sentence composition is retained; no duplicate rows per occurrence.
        EXPECT_EQ(2u, intent_stage_physicality_count(stage.get()));
        if (!all_present) {
            size_t size = 0, offset = 0;
            const uint8_t* bytes = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ENTITIES, &size);
            std::vector<Field> fields;
            ASSERT_TRUE(next_row(bytes, size, offset, fields));
            ASSERT_EQ(3u, fields.size());
            ASSERT_EQ(16, fields[0].length);
            EXPECT_EQ(0, std::memcmp(fields[0].bytes, &expected_word.id, 16));
            EXPECT_EQ(size, offset);
        }
        ASSERT_EQ(0, content_witness_emit_tree(stage.get(), tree.get(), &source, bitmap.data(), nodes, &replay));
        EXPECT_TRUE(hash128_equals(&root, &replay));
        EXPECT_EQ(all_present ? 0u : 1u, intent_stage_entity_count(stage.get()));
        // The replay stages the same two forms again; the apply converges them by id.
        EXPECT_EQ(4u, intent_stage_physicality_count(stage.get()));
    }
}

TEST(LaplaceContentObservations, InvalidBitmapAndEmptyTreeDoNotEmitRows) {
    Stage stage(intent_stage_new(0), intent_stage_free);
    Tree tree(tier_tree_new(1), tier_tree_free);
    const hash128_t source{101, 102};
    hash128_t root{};
    ASSERT_EQ(-2, content_witness_emit_tree(stage.get(), tree.get(), &source, nullptr, 0, &root));
    tier_tree_t* raw_tree = nullptr;
    ASSERT_EQ(0, content_witness_tree_build(reinterpret_cast<const uint8_t*>("ab"), 2, &raw_tree));
    tree.reset(raw_tree);
    const uint8_t bitmap = 0;
    EXPECT_EQ(-2, content_witness_emit_tree(stage.get(), tree.get(), &source, &bitmap, 1, &root));
    EXPECT_EQ(0u, intent_stage_entity_count(stage.get()));
    EXPECT_EQ(0u, intent_stage_physicality_count(stage.get()));
}

TEST(LaplaceContentObservations, ExplicitAtomicRootsCopyFloorDespiteExistingWitnessesAndBitmap) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    const hash128_t source{101, 102};
    for (const char text : {'a', ' ', '7'}) {
        SCOPED_TRACE(text);
        const auto* record = codepoint_table_lookup(static_cast<uint32_t>(text));
        ASSERT_NE(record, nullptr);
        const codepoint_entry_t floor = *record;
        tier_tree_t* raw_tree = nullptr;
        ASSERT_EQ(content_witness_tree_build(reinterpret_cast<const uint8_t*>(&text), 1, &raw_tree), 0);
        Tree tree(raw_tree, tier_tree_free);
        tier_node_view_t root{};
        ASSERT_EQ(content_witness_tree_root_node(tree.get(), &root), 0);
        ASSERT_EQ(root.tier, 0u);
        ASSERT_TRUE(hash128_equals(&root.id, &floor.hash));
        const size_t nodes = tier_tree_node_count(tree.get());
        std::vector<uint8_t> existing((nodes + 7u) / 8u, 0xff);
        Stage stage(intent_stage_new(0), intent_stage_free);
        ASSERT_NE(stage, nullptr);
        hash128_t placement{};
        laplace_physicality_id_compute(floor.hash, 1, &placement);
        ASSERT_EQ(intent_stage_witness_record(stage.get(), &floor.hash), 0);
        ASSERT_EQ(intent_stage_witness_record(stage.get(), &placement), 0);
        for (size_t observation = 0; observation < 2; ++observation) {
            hash128_t emitted{};
            ASSERT_EQ(content_witness_emit_tree(stage.get(), tree.get(), &source,
                existing.data(), nodes, &emitted), 0);
            EXPECT_TRUE(hash128_equals(&emitted, &floor.hash));
            EXPECT_EQ(intent_stage_entity_count(stage.get()), 0u);
            EXPECT_EQ(intent_stage_physicality_count(stage.get()), observation + 1u);
        }
        size_t size = 0, offset = 0;
        const auto* bytes = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &size);
        std::vector<Field> fields;
        for (size_t observation = 0; observation < 2; ++observation) {
            ASSERT_TRUE(next_row(bytes, size, offset, fields));
            expect_atomic_body(fields, floor, INTENT_STAGE_PG_EPOCH_UNIX_US);
        }
        EXPECT_EQ(offset, size);
    }
}

TEST(LaplaceContentObservations, AtomicRootReplaySharesDescriptorAndPreservesEveryStructuralObservation) {
    constexpr size_t budget = 64u * 1024u * 1024u;
    const hash128_t source_a{101, 102}, source_b{201, 202}, unit_one{301, 302}, unit_two{401, 402};
    const physicality_descriptor_source_observation_t a{source_a, unit_one, 0.8};
    const physicality_descriptor_source_observation_t b{source_b, unit_one, 0.9};
    const physicality_descriptor_source_observation_t another_unit{source_a, unit_two, 0.8};
    const std::array<physicality_descriptor_source_observation_t, 5> sources{a, a, b, b, another_unit};
    tier_tree_t* raw_tree = nullptr;
    ASSERT_EQ(content_witness_tree_build(reinterpret_cast<const uint8_t*>("a"), 1, &raw_tree), 0);
    Tree tree(raw_tree, tier_tree_free);
    const auto* floor = codepoint_table_lookup('a');
    ASSERT_NE(floor, nullptr);
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    // The source/unit array binds the five actual appended rows externally.
    for (const auto& source : sources) {
        hash128_t root{};
        ASSERT_EQ(content_witness_emit_tree(stage.get(), tree.get(), &source.source_id,
            nullptr, 0, &root), 0);
        EXPECT_TRUE(hash128_equals(&root, &floor->hash));
    }
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 0u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), sources.size());
    physicality_descriptor_vocabulary_t* raw_vocabulary = nullptr;
    ASSERT_EQ(physicality_descriptor_vocabulary_create(&source_a, budget, &raw_vocabulary),
        PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_vocabulary_t, decltype(&physicality_descriptor_vocabulary_free)>
        vocabulary(raw_vocabulary, physicality_descriptor_vocabulary_free);
    const intent_stage_t* source_stage = stage.get();
    const physicality_descriptor_limits_t limits{budget};
    physicality_descriptor_capture_t* raw_capture = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages(&source_stage, 1,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits, budget, &raw_capture),
        PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>
        capture(raw_capture, physicality_descriptor_capture_free);
    size_t count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(capture.get(), &count);
    ASSERT_EQ(count, sources.size());
    for (size_t i = 0; i < count; ++i) {
        EXPECT_TRUE(hash128_equals(&bodies[i].entity_id, &floor->hash));
        EXPECT_EQ(bodies[i].type, 1);
        EXPECT_EQ(std::memcmp(bodies[i].coord, floor->coord, sizeof(floor->coord)), 0);
        EXPECT_EQ(std::memcmp(&bodies[i].hilbert_index, &floor->hilbert, sizeof(floor->hilbert)), 0);
        EXPECT_EQ(bodies[i].trajectory_vertices, 0u);
        EXPECT_EQ(bodies[i].n_constituents, 0);
        EXPECT_TRUE(bodies[i].alignment_residual_is_null);
        EXPECT_TRUE(bodies[i].source_dim_is_null);
    }
    const auto* descriptors = physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(capture.get()), &count);
    ASSERT_EQ(count, sources.size());
    for (size_t i = 1; i < count; ++i)
        EXPECT_TRUE(hash128_equals(&descriptors[i], &descriptors[0]));
    physicality_descriptor_materialization_t* raw_materialized = nullptr;
    ASSERT_EQ(physicality_descriptor_materialize(capture.get(), vocabulary.get(),
        nullptr, 0, nullptr, 0, nullptr, 0, sources.data(), sources.size(),
        INTENT_STAGE_PG_EPOCH_UNIX_US, budget, &raw_materialized), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_materialization_t, decltype(&physicality_descriptor_materialization_free)>
        materialized(raw_materialized, physicality_descriptor_materialization_free);
    size_t provenance_count = 0;
    const auto* provenance =
        physicality_descriptor_materialization_observations(materialized.get(), &provenance_count);
    ASSERT_NE(provenance, nullptr);
    ASSERT_EQ(provenance_count, sources.size());
    for (size_t i = 0; i < provenance_count; ++i) {
        EXPECT_TRUE(hash128_equals(&provenance[i].entity_id, &floor->hash));
        EXPECT_TRUE(hash128_equals(&provenance[i].descriptor_id, &descriptors[i]));
        EXPECT_TRUE(hash128_equals(&provenance[i].source_id, &sources[i].source_id));
        EXPECT_TRUE(hash128_equals(&provenance[i].source_unit_id, &sources[i].source_unit_id));
    }
    Stage generated(physicality_descriptor_materialization_take_stage(materialized.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    EXPECT_EQ(intent_stage_attestation_count(generated.get()), 0u);
}

TEST(LaplaceContentObservations, FloorAtomRejectsInvalidIdentityAndMissingFloorWithoutAppending) {
    const auto* floor = codepoint_table_lookup('a');
    ASSERT_NE(floor, nullptr);
    const hash128_t expected = floor->hash, wrong{901, 902}, source{101, 102};
    const int64_t observed_at = INTENT_STAGE_PG_EPOCH_UNIX_US + 123;
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    ASSERT_EQ(content_witness_emit_floor_atom(stage.get(), 'a', &expected, observed_at), 0);
    size_t size = 0;
    const auto* bytes = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &size);
    const std::vector<uint8_t> before(bytes, bytes + size);
    EXPECT_EQ(content_witness_emit_floor_atom(nullptr, 'a', &expected, observed_at), -1);
    EXPECT_EQ(content_witness_emit_floor_atom(stage.get(), 'a', nullptr, observed_at), -1);
    EXPECT_EQ(content_witness_emit_floor_atom(stage.get(), 'a', &wrong, observed_at), -2);
    EXPECT_EQ(content_witness_emit_floor_atom(stage.get(), UINT32_MAX, &expected, observed_at), -2);
    tier_tree_t* raw_tree = nullptr;
    ASSERT_EQ(content_witness_tree_build(reinterpret_cast<const uint8_t*>("a"), 1, &raw_tree), 0);
    Tree tree(raw_tree, tier_tree_free);
    // A built singleton still has to prove its root E matches its declared
    // floor atom when it crosses the observation boundary.
    for (size_t i = 0; i < tier_tree_node_count(tree.get()); ++i) {
        if (tier_tree_tier_array(tree.get())[i] == 0) {
            ASSERT_EQ(tier_tree_set_id(tree.get(), static_cast<uint32_t>(i), &wrong), 0);
        }
    }
    hash128_t root{};
    ASSERT_EQ(content_witness_tree_root_id(tree.get(), &root), 0);
    ASSERT_TRUE(hash128_equals(&root, &wrong));
    EXPECT_EQ(content_witness_emit_tree(stage.get(), tree.get(), &source, nullptr, 0, &root), -2);
    for (size_t i = 0; i < tier_tree_node_count(tree.get()); ++i) {
        if (tier_tree_tier_array(tree.get())[i] == 0) {
            ASSERT_EQ(tier_tree_set_id(tree.get(), static_cast<uint32_t>(i), &expected), 0);
        }
    }
    struct RestoreFloor {
        ~RestoreFloor() { EXPECT_EQ(codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS), 0); }
    } restore;
    codepoint_table_unload();
    EXPECT_EQ(content_witness_emit_floor_atom(stage.get(), 'a', &expected, observed_at), -3);
    EXPECT_EQ(content_witness_emit_tree(stage.get(), tree.get(), &source, nullptr, 0, &root), -3);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 0u);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 1u);
    bytes = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &size);
    ASSERT_EQ(size, before.size());
    EXPECT_EQ(std::memcmp(bytes, before.data(), size), 0);
}

TEST(LaplaceContentObservations, CompositeRootsDoNotObserveEveryInteriorFloorAtom) {
    const hash128_t source{101, 102};
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    hash128_t root{};
    ASSERT_EQ(content_witness_batch_add(stage.get(), reinterpret_cast<const uint8_t*>("ab"),
        2, &source, &root), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 1u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), 1u);
    size_t size = 0, offset = 0;
    const auto* bytes = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &size);
    std::vector<Field> fields;
    ASSERT_TRUE(next_row(bytes, size, offset, fields));
    ASSERT_EQ(fields.size(), 10u);
    ASSERT_EQ(fields[1].length, 16);
    EXPECT_EQ(std::memcmp(fields[1].bytes, &root, 16), 0);
    EXPECT_GT(fields[5].length, 0);
    ASSERT_EQ(fields[6].length, 4);
    EXPECT_EQ(be32(fields[6].bytes), 2u);
    EXPECT_EQ(offset, size);
}
