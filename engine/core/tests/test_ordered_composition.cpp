#include <gtest/gtest.h>

#include <array>
#include <cstddef>
#include <cmath>
#include <cstring>
#include <memory>
#include <vector>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/ordered_composition.h"
#include "laplace/core/physicality_descriptor_admission.h"

namespace {

static_assert(offsetof(laplace_ordered_composition_result_t, first_physicality_row) == 72u);
static_assert(offsetof(laplace_ordered_composition_result_t, emitted_physicality_rows) == 80u);
static_assert(sizeof(laplace_ordered_composition_result_t) == 88u);

using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
constexpr size_t kDescriptorBudget = 64u * 1024u * 1024u;

hash128_t hash(const char* text) {
    hash128_t id;
    hash128_blake3(reinterpret_cast<const uint8_t*>(text), std::strlen(text), &id);
    return id;
}

laplace_ordered_component_t component(const char* text, uint8_t tier, double x) {
    laplace_ordered_component_t result{};
    result.id = hash(text);
    result.tier = tier;
    result.coord[0] = x;
    result.coord[1] = x + 1.0;
    result.coord[2] = x + 2.0;
    result.coord[3] = x + 3.0;
    return result;
}

laplace_ordered_composition_request_t request(
    const laplace_ordered_component_t* components, size_t n) {
    laplace_ordered_composition_request_t result{};
    result.components = components;
    result.component_count = n;
    result.type_id = hash("test/ordered/type");
    result.source_id = hash("test/ordered/source");
    result.observed_at_unix_us = INTENT_STAGE_PG_EPOCH_UNIX_US;
    return result;
}

uint32_t be32(const uint8_t* p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16)
         | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

uint32_t le32(const uint8_t* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

int16_t be16(const uint8_t* p) {
    return (int16_t)(((uint16_t)p[0] << 8) | p[1]);
}

const uint8_t* row_at(const intent_stage_t* stage, intent_stage_table_t table,
                      size_t wanted, size_t* out_len) {
    size_t bytes = 0, offset = 0;
    const auto* data = intent_stage_tuple_ptr(stage, table, &bytes);
    for (size_t row = 0; offset < bytes; ++row) {
        const size_t first = offset;
        if (bytes - offset < 2u) return nullptr;
        const uint16_t columns = static_cast<uint16_t>(be16(data + offset));
        offset += 2u;
        for (uint16_t column = 0; column < columns; ++column) {
            if (bytes - offset < 4u) return nullptr;
            const int32_t length = static_cast<int32_t>(be32(data + offset));
            offset += 4u;
            if (length == -1) continue;
            if (length < 0 || static_cast<size_t>(length) > bytes - offset) return nullptr;
            offset += static_cast<size_t>(length);
        }
        if (row == wanted) {
            *out_len = offset - first;
            return data + first;
        }
    }
    return nullptr;
}

const uint8_t* field(const uint8_t* row, size_t row_len, uint16_t wanted,
                     uint32_t* out_len) {
    if (row_len < 2) return nullptr;
    const uint16_t columns = (uint16_t)((row[0] << 8) | row[1]);
    size_t offset = 2;
    for (uint16_t i = 0; i < columns; ++i) {
        if (offset + 4 > row_len) return nullptr;
        const int32_t length = (int32_t)be32(row + offset);
        offset += 4;
        if (length == -1) {
            if (i == wanted) return nullptr;
            continue;
        }
        if (length < 0 || offset + (size_t)length > row_len) return nullptr;
        if (i == wanted) {
            *out_len = (uint32_t)length;
            return row + offset;
        }
        offset += (size_t)length;
    }
    return nullptr;
}

double le_double(const uint8_t* p) {
    uint64_t bits = (uint64_t)p[0] | ((uint64_t)p[1] << 8)
        | ((uint64_t)p[2] << 16) | ((uint64_t)p[3] << 24)
        | ((uint64_t)p[4] << 32) | ((uint64_t)p[5] << 40)
        | ((uint64_t)p[6] << 48) | ((uint64_t)p[7] << 56);
    double result;
    std::memcpy(&result, &bits, sizeof(result));
    return result;
}

void expect_trajectory(const intent_stage_t* stage,
                       const laplace_ordered_component_t* children, size_t n,
                       size_t row_index = 0u) {
    size_t byte_count = 0;
    const uint8_t* rows = row_at(stage, INTENT_STAGE_TABLE_PHYSICALITIES, row_index, &byte_count);
    ASSERT_NE(nullptr, rows);
    uint32_t trajectory_len = 0;
    const uint8_t* trajectory = field(rows, byte_count, 5, &trajectory_len);
    ASSERT_NE(nullptr, trajectory);
    size_t stored = 0;
    for (size_t i = 0; i < n;) {
        ++stored;
        const uint64_t flags = laplace_vertex_flags(
            children[i].tier, children[i].has_atom != 0, children[i].atom);
        size_t run = 1;
        while (i + run < n && hash128_equals(&children[i].id, &children[i + run].id)
               && flags == laplace_vertex_flags(children[i + run].tier,
                   children[i + run].has_atom != 0, children[i + run].atom)) ++run;
        i += run;
    }
    ASSERT_EQ(9u + 32u * stored, trajectory_len);
    ASSERT_EQ(1, trajectory[0]);
    ASSERT_EQ(stored, le32(trajectory + 5));
    size_t child = 0;
    for (size_t i = 0; i < stored; ++i) {
        double vertex[4];
        for (size_t k = 0; k < 4; ++k)
            vertex[k] = le_double(trajectory + 9 + i * 32 + k * 8);
        mantissa_payload_t payload;
        mantissa_unpack(vertex, &payload);
        size_t run = 1;
        const uint64_t flags = laplace_vertex_flags(
            children[child].tier, children[child].has_atom != 0, children[child].atom);
        while (child + run < n && hash128_equals(&children[child].id, &children[child + run].id)
               && flags == laplace_vertex_flags(children[child + run].tier,
                   children[child + run].has_atom != 0, children[child + run].atom)) ++run;
        EXPECT_TRUE(hash128_equals(&children[child].id, &payload.entity_id));
        EXPECT_EQ(child + 1, payload.ordinal);
        EXPECT_EQ(run, payload.run_length);
        EXPECT_EQ(laplace_vertex_flags(
            children[child].tier, children[child].has_atom != 0, children[child].atom),
            payload.flags);
        child += run;
    }
}

void expect_physicality(const intent_stage_t* stage,
                         const laplace_ordered_composition_request_t& request,
                         const laplace_ordered_composition_result_t& result) {
    ASSERT_EQ(result.emitted_physicality_rows, 1u);
    size_t bytes = 0;
    const auto* row = row_at(stage, INTENT_STAGE_TABLE_PHYSICALITIES,
        result.first_physicality_row, &bytes);
    ASSERT_NE(row, nullptr);
    uint32_t width = 0;
    const auto* entity = field(row, bytes, 1, &width);
    ASSERT_NE(entity, nullptr);
    ASSERT_EQ(width, sizeof(result.id));
    EXPECT_EQ(std::memcmp(entity, &result.id, width), 0);
    hash128_t placement{};
    laplace_physicality_id_compute(result.id, 1, &placement);
    const auto* physicality = field(row, bytes, 0, &width);
    ASSERT_NE(physicality, nullptr);
    ASSERT_EQ(width, sizeof(placement));
    EXPECT_EQ(std::memcmp(physicality, &placement, width), 0);
    const auto* coord = field(row, bytes, 3, &width);
    ASSERT_NE(coord, nullptr);
    ASSERT_EQ(width, 37u);
    ASSERT_EQ(coord[0], 1u);
    ASSERT_EQ(le32(coord + 1), 0xC0000001u);
    for (size_t axis = 0; axis < 4; ++axis)
        EXPECT_DOUBLE_EQ(le_double(coord + 5 + axis * 8), result.coord[axis]);
    const auto* hilbert = field(row, bytes, 4, &width);
    ASSERT_NE(hilbert, nullptr);
    ASSERT_EQ(width, sizeof(result.hilbert));
    EXPECT_EQ(std::memcmp(hilbert, &result.hilbert, width), 0);
    const auto* time = field(row, bytes, 9, &width);
    ASSERT_NE(time, nullptr);
    ASSERT_EQ(width, sizeof(int64_t));
    uint64_t bits = 0;
    for (size_t i = 0; i < width; ++i) bits = (bits << 8u) | time[i];
    int64_t timestamp;
    std::memcpy(&timestamp, &bits, sizeof(timestamp));
    EXPECT_EQ(timestamp + INTENT_STAGE_PG_EPOCH_UNIX_US, request.observed_at_unix_us);
    const auto* type = field(row, bytes, 2, &width);
    ASSERT_NE(type, nullptr);
    ASSERT_EQ(width, 2u);
    EXPECT_EQ(be16(type), 1);
    const auto* constituents = field(row, bytes, 6, &width);
    ASSERT_NE(constituents, nullptr);
    ASSERT_EQ(width, 4u);
    EXPECT_EQ(be32(constituents), request.component_count == 1 ? 0u : request.component_count);
    EXPECT_EQ(field(row, bytes, 7, &width), nullptr);
    EXPECT_EQ(field(row, bytes, 8, &width), nullptr);
    if (request.component_count == 1) {
        EXPECT_EQ(field(row, bytes, 5, &width), nullptr);
    } else {
        expect_trajectory(stage, request.components, request.component_count, result.first_physicality_row);
    }
}

int16_t only_entity_tier(const intent_stage_t* stage) {
    size_t byte_count = 0;
    const uint8_t* rows = intent_stage_tuple_ptr(stage, INTENT_STAGE_TABLE_ENTITIES, &byte_count);
    uint32_t tier_len = 0;
    const uint8_t* tier = field(rows, byte_count, 1, &tier_len);
    EXPECT_EQ(2u, tier_len);
    return tier ? be16(tier) : -1;
}

TEST(OrderedCompositionStage, UnequalChildFloorsDeriveParentAndStageOnce) {
    laplace_ordered_component_t children[] = {
        component("content", 2, -1.0), component("metadata", 5, 1.0)};
    const auto r = request(children, 2);
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(nullptr, stage);

    laplace_ordered_composition_result_t resolved{};
    ASSERT_EQ(0, laplace_ordered_composition_compose_batch(&r, 1, &resolved));
    EXPECT_EQ(6, resolved.tier);
    EXPECT_DOUBLE_EQ(0.0, resolved.coord[0]);
    EXPECT_DOUBLE_EQ(1.0, resolved.coord[1]);
    EXPECT_DOUBLE_EQ(2.0, resolved.coord[2]);
    EXPECT_DOUBLE_EQ(3.0, resolved.coord[3]);
    EXPECT_EQ(resolved.first_physicality_row, 0u);
    EXPECT_EQ(resolved.emitted_physicality_rows, 0u);

    hash128_t expected;
    hash128_t child_ids[] = {children[0].id, children[1].id};
    double child_coords[8] = {-1.0, 0.0, 1.0, 2.0, 1.0, 2.0, 3.0, 4.0};
    double parent_coord[4];
    hilbert128_t parent_hilbert;
    hash_composer_compose_node(6, child_ids, child_coords, 2,
                               &expected, parent_coord, &parent_hilbert);
    EXPECT_TRUE(hash128_equals(&expected, &resolved.id));

    laplace_ordered_composition_result_t staged{};
    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, &r, 1, &staged));
    EXPECT_TRUE(hash128_equals(&resolved.id, &staged.id));
    EXPECT_EQ(resolved.tier, staged.tier);
    EXPECT_EQ(1u, intent_stage_entity_count(stage));
    EXPECT_EQ(1u, intent_stage_physicality_count(stage));
    EXPECT_EQ(staged.first_physicality_row, 0u);
    expect_physicality(stage, r, staged);
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, OrderAndRepeatedChildrenAreIdentitySignificant) {
    const auto a = component("a", 1, 0.0);
    const auto b = component("b", 3, 1.0);
    laplace_ordered_component_t aba[] = {a, b, a};
    laplace_ordered_component_t aab[] = {a, a, b};
    laplace_ordered_composition_request_t requests[] = {request(aba, 3), request(aab, 3)};
    intent_stage_t* stage = intent_stage_new(8);
    ASSERT_NE(nullptr, stage);

    laplace_ordered_composition_result_t results[2]{};
    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, requests, 2, results));
    EXPECT_FALSE(hash128_equals(&results[0].id, &results[1].id));
    EXPECT_EQ(4, results[0].tier);
    EXPECT_EQ(4, results[1].tier);
    EXPECT_EQ(2u, intent_stage_entity_count(stage));
    EXPECT_EQ(2u, intent_stage_physicality_count(stage));
    expect_trajectory(stage, aba, 3);
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, RepeatedSourceConstituentsPersistAsCompactFlaggedRuns) {
    const auto whitespace = component("source-space", 1, 0.0);
    const auto token = component("source-token", 47, 1.0);
    laplace_ordered_component_t children[] = { whitespace, whitespace, whitespace, token,
                                                whitespace, whitespace };
    const auto r = request(children, 6);
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(nullptr, stage);
    laplace_ordered_composition_result_t result{};
    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, &r, 1, &result));
    ASSERT_EQ(1u, intent_stage_physicality_count(stage));
    expect_trajectory(stage, children, 6);
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, SingletonReturnsChildWithoutSelfWrapper) {
    const auto only = component("only", 4, 2.0);
    const auto r = request(&only, 1);
    intent_stage_t* stage = intent_stage_new(2);
    ASSERT_NE(nullptr, stage);

    laplace_ordered_composition_result_t result{};
    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, &r, 1, &result));
    EXPECT_TRUE(hash128_equals(&only.id, &result.id));
    EXPECT_EQ(only.tier, result.tier);
    EXPECT_EQ(0u, intent_stage_entity_count(stage));
    EXPECT_EQ(0u, intent_stage_physicality_count(stage));
    EXPECT_EQ(result.first_physicality_row, 0u);
    EXPECT_EQ(result.emitted_physicality_rows, 0u);
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, TierZeroRequiresPackedAtom) {
    auto atom = component("A", 0, 0.0);
    const auto invalid = request(&atom, 1);
    laplace_ordered_composition_result_t result{};
    EXPECT_NE(0, laplace_ordered_composition_compose_batch(&invalid, 1, &result));

    atom.has_atom = 1;
    atom.atom = 'A';
    const auto valid = request(&atom, 1);
    ASSERT_EQ(0, laplace_ordered_composition_compose_batch(&valid, 1, &result));
    EXPECT_TRUE(hash128_equals(&atom.id, &result.id));
}

TEST(OrderedCompositionStage, BatchDeduplicatesEntitiesAndRetainsIdenticalRawPhysicalities) {
    laplace_ordered_component_t children[] = {
        component("content", 2, -1.0), component("metadata", 5, 1.0)};
    laplace_ordered_composition_request_t requests[] = {request(children, 2), request(children, 2)};
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(nullptr, stage);

    laplace_ordered_composition_result_t results[2]{};
    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, requests, 2, results));
    EXPECT_TRUE(hash128_equals(&results[0].id, &results[1].id));
    EXPECT_EQ(results[0].tier, results[1].tier);
    EXPECT_EQ(1u, intent_stage_entity_count(stage));
    EXPECT_EQ(2u, intent_stage_physicality_count(stage));
    EXPECT_EQ(results[0].first_physicality_row, 0u);
    EXPECT_EQ(results[1].first_physicality_row, 1u);
    expect_physicality(stage, requests[0], results[0]);
    expect_physicality(stage, requests[1], results[1]);
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, SameIdentityAtDifferentFloorsStagesTheMinimumFloor) {
    laplace_ordered_component_t higher[] = {
        component("left", 4, 10.0), component("right", 5, 12.0)};
    laplace_ordered_component_t lower[] = {
        component("left", 1, 0.0), component("right", 2, 1.0)};
    auto high_request = request(higher, 2);
    auto low_request = request(lower, 2);
    high_request.observed_at_unix_us += 20;
    low_request.observed_at_unix_us += 10;
    for (const bool reversed : {false, true}) {
        SCOPED_TRACE(reversed);
        laplace_ordered_composition_request_t requests[] = {
            reversed ? low_request : high_request, reversed ? high_request : low_request};
        laplace_ordered_composition_result_t results[2]{};
        Stage stage(intent_stage_new(4), intent_stage_free);
        ASSERT_NE(nullptr, stage);
        ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage.get(), requests, 2, results));
        const size_t low_index = reversed ? 0u : 1u;
        const size_t high_index = 1u - low_index;
        EXPECT_TRUE(hash128_equals(&results[0].id, &results[1].id));
        EXPECT_EQ(6, results[high_index].tier);
        EXPECT_EQ(3, results[low_index].tier);
        EXPECT_EQ(1u, intent_stage_entity_count(stage.get()));
        EXPECT_EQ(2u, intent_stage_physicality_count(stage.get()));
        EXPECT_EQ(3, only_entity_tier(stage.get()));
        EXPECT_EQ(results[low_index].first_physicality_row, 0u);
        EXPECT_EQ(results[high_index].first_physicality_row, 1u);
        expect_physicality(stage.get(), requests[low_index], results[low_index]);
        expect_physicality(stage.get(), requests[high_index], results[high_index]);
        expect_trajectory(stage.get(), lower, 2);
    }
}

TEST(OrderedCompositionStage, SeparateCallsLowerAnAlreadyStagedFloor) {
    laplace_ordered_component_t higher[] = {
        component("left", 4, 0.0), component("right", 5, 1.0)};
    laplace_ordered_component_t lower[] = {
        component("left", 1, 0.0), component("right", 2, 1.0)};
    const auto high_request = request(higher, 2);
    const auto low_request = request(lower, 2);
    laplace_ordered_composition_result_t high{}, low{};
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(nullptr, stage);

    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, &high_request, 1, &high));
    ASSERT_EQ(6, only_entity_tier(stage));
    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, &low_request, 1, &low));
    EXPECT_TRUE(hash128_equals(&high.id, &low.id));
    EXPECT_EQ(1u, intent_stage_entity_count(stage));
    EXPECT_EQ(2u, intent_stage_physicality_count(stage));
    EXPECT_EQ(3, only_entity_tier(stage));
    EXPECT_EQ(high.first_physicality_row, 0u);
    EXPECT_EQ(low.first_physicality_row, 1u);
    expect_physicality(stage, high_request, high);
    expect_physicality(stage, low_request, low);
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, AlternateGeometryRetainsBothBodiesAndReportsActualAppendSpans) {
    laplace_ordered_component_t original[] = {
        component("left", 1, 0.0), component("right", 2, 1.0)};
    laplace_ordered_component_t changed[] = {original[0], original[1]};
    changed[0].coord[0] += 0.25;
    changed[1].coord[2] -= 0.5;
    laplace_ordered_composition_request_t requests[] = {
        request(original, 2), request(changed, 2), request(original, 1)};
    requests[0].observed_at_unix_us += 10;
    requests[1].observed_at_unix_us += 20;
    laplace_ordered_composition_result_t results[3]{};
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), requests, 3, results), 0);
    EXPECT_TRUE(hash128_equals(&results[0].id, &results[1].id));
    EXPECT_NE(std::memcmp(results[0].coord, results[1].coord, sizeof(results[0].coord)), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 1u);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 2u);
    EXPECT_EQ(results[0].first_physicality_row, 0u);
    EXPECT_EQ(results[1].first_physicality_row, 1u);
    expect_physicality(stage.get(), requests[0], results[0]);
    expect_physicality(stage.get(), requests[1], results[1]);
    EXPECT_TRUE(hash128_equals(&results[2].id, &original[0].id));
    EXPECT_EQ(results[2].emitted_physicality_rows, 0u);

    laplace_ordered_composition_result_t replay{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), &requests[1], 1, &replay), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 1u);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 3u);
    EXPECT_EQ(replay.first_physicality_row, 2u);
    expect_physicality(stage.get(), requests[1], replay);
}

TEST(OrderedCompositionStage, ExactReplayRetainsRawRowsAndReusesDescriptorAndSourceUnitWitness) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    laplace_ordered_component_t children[2]{};
    for (size_t i = 0; i < 2; ++i) {
        children[i].atom = static_cast<uint32_t>('a' + i);
        children[i].has_atom = 1;
        hilbert128_t ignored;
        ASSERT_EQ(codepoint_table_resolve_atom(children[i].atom,
            &children[i].id, children[i].coord, &ignored), 0);
    }
    auto first_request = request(children, 2);
    first_request.observed_at_unix_us += 3;
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    laplace_ordered_composition_result_t first{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), &first_request, 1, &first), 0);

    physicality_descriptor_vocabulary_t* vocabulary_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_vocabulary_create(&first_request.source_id,
        kDescriptorBudget, &vocabulary_raw), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_vocabulary_t, decltype(&physicality_descriptor_vocabulary_free)>
        vocabulary(vocabulary_raw, physicality_descriptor_vocabulary_free);
    const auto* basis = physicality_descriptor_vocabulary_basis(vocabulary.get());
    const physicality_descriptor_limits_t limits{kDescriptorBudget};
    const intent_stage_t* source_stage = stage.get();
    physicality_descriptor_capture_t* first_capture_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages(&source_stage, 1, basis,
        &limits, kDescriptorBudget, &first_capture_raw), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>
        first_capture(first_capture_raw, physicality_descriptor_capture_free);
    size_t count = 0;
    const auto* first_roots = physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(first_capture.get()), &count);
    ASSERT_EQ(count, 1u);
    const hash128_t descriptor = first_roots[0];

    laplace_ordered_composition_request_t replays[] = {first_request, first_request};
    replays[0].observed_at_unix_us += 6;
    replays[1].observed_at_unix_us += 2;
    laplace_ordered_composition_result_t replay_results[2]{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), replays, 2, replay_results), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 1u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), 3u);
    EXPECT_EQ(replay_results[0].first_physicality_row, 1u);
    EXPECT_EQ(replay_results[1].first_physicality_row, 2u);
    expect_physicality(stage.get(), first_request, first);
    expect_physicality(stage.get(), replays[0], replay_results[0]);
    expect_physicality(stage.get(), replays[1], replay_results[1]);
    physicality_descriptor_capture_t* captured_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages(&source_stage, 1, basis,
        &limits, kDescriptorBudget, &captured_raw), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>
        captured(captured_raw, physicality_descriptor_capture_free);
    const auto* roots = physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(captured.get()), &count);
    ASSERT_EQ(count, 3u);
    for (size_t i = 0; i < count; ++i)
        EXPECT_TRUE(hash128_equals(&roots[i], &descriptor));

    const physicality_descriptor_source_observation_t witness{
        first_request.source_id, hash("test/ordered/source-unit"), 0.8};
    const std::array<physicality_descriptor_source_observation_t, 3> witnesses{witness, witness, witness};
    physicality_descriptor_materialization_t* materialized_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_materialize(captured.get(), vocabulary.get(),
        nullptr, 0, nullptr, 0, nullptr, 0, witnesses.data(), witnesses.size(),
        &first_request.source_id, first_request.observed_at_unix_us, kDescriptorBudget,
        &materialized_raw), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_materialization_t, decltype(&physicality_descriptor_materialization_free)>
        materialized(materialized_raw, physicality_descriptor_materialization_free);
    Stage generated(physicality_descriptor_materialization_take_stage(materialized.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    ASSERT_EQ(intent_stage_attestation_count(generated.get()), 1u);
    size_t bytes = 0;
    const auto* row = row_at(generated.get(), INTENT_STAGE_TABLE_ATTESTATIONS, 0, &bytes);
    ASSERT_NE(row, nullptr);
    hash128_t relation{};
    ASSERT_EQ(laplace_relation_resolve("HAS_PHYSICALITY", &relation), 0);
    uint32_t width = 0;
    const auto* relation_field = field(row, bytes, 2, &width);
    ASSERT_NE(relation_field, nullptr);
    ASSERT_EQ(width, sizeof(relation));
    EXPECT_EQ(std::memcmp(relation_field, &relation, width), 0);
    const auto* object = field(row, bytes, 3, &width);
    ASSERT_NE(object, nullptr);
    ASSERT_EQ(width, sizeof(descriptor));
    EXPECT_EQ(std::memcmp(object, &descriptor, width), 0);
    const auto* observations = field(row, bytes, 8, &width);
    ASSERT_NE(observations, nullptr);
    ASSERT_EQ(width, 8u);
    uint64_t observation_count = 0;
    for (size_t i = 0; i < width; ++i) observation_count = (observation_count << 8u) | observations[i];
    EXPECT_EQ(observation_count, 1u);
    const auto* time = field(row, bytes, 7, &width);
    ASSERT_NE(time, nullptr);
    ASSERT_EQ(width, 8u);
    uint64_t bits = 0;
    for (size_t i = 0; i < width; ++i) bits = (bits << 8u) | time[i];
    int64_t observed_at;
    std::memcpy(&observed_at, &bits, sizeof(observed_at));
    EXPECT_EQ(observed_at + INTENT_STAGE_PG_EPOCH_UNIX_US, replays[0].observed_at_unix_us);
}

TEST(OrderedCompositionStage, InvalidBatchDoesNotPartiallyStage) {
    laplace_ordered_component_t valid_children[] = {
        component("left", 1, 0.0), component("right", 2, 1.0)};
    laplace_ordered_component_t invalid_children[] = {
        component("finite", 1, 0.0), component("nan", 2, NAN)};
    laplace_ordered_composition_request_t requests[] = {
        request(valid_children, 2), request(invalid_children, 2)};
    laplace_ordered_composition_result_t results[2]{};
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(nullptr, stage);

    EXPECT_NE(0, laplace_ordered_composition_stage_batch(stage, requests, 2, results));
    EXPECT_EQ(0u, intent_stage_entity_count(stage));
    EXPECT_EQ(0u, intent_stage_physicality_count(stage));
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, WitnessBudgetFailureCannotReportSuccessfulObservation) {
    laplace_ordered_component_t children[] = {
        component("left", 1, 0.0), component("right", 2, 1.0)};
    const auto value = request(children, 2);
    laplace_ordered_composition_result_t result{};
    // The COPY rows fit, but the first stage-owned witness table does not.
    constexpr size_t budget = 64u * 1024u;
    Stage stage(intent_stage_new_bounded(0, budget), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    EXPECT_EQ(-3, laplace_ordered_composition_stage_batch(stage.get(), &value, 1, &result));
    EXPECT_TRUE(intent_stage_allocation_failed(stage.get()));
    EXPECT_EQ(1u, intent_stage_physicality_count(stage.get()));
    EXPECT_LE(intent_stage_memory_peak_bytes(stage.get()), budget);
}

TEST(OrderedCompositionStage, FloorSingletonCopiesExactLoadedBodyWithoutWrapperOrEntity) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    const auto* floor = codepoint_table_lookup('a');
    ASSERT_NE(floor, nullptr);
    laplace_ordered_component_t atom{};
    atom.id = floor->hash;
    atom.atom = 'a';
    atom.has_atom = 1;
    std::memcpy(atom.coord, floor->coord, sizeof(atom.coord));
    auto value = request(&atom, 1);
    value.observed_at_unix_us += 123;
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    hash128_t placement{};
    laplace_physicality_id_compute(atom.id, 1, &placement);
    ASSERT_EQ(intent_stage_witness_record(stage.get(), &atom.id), 0);
    ASSERT_EQ(intent_stage_witness_record(stage.get(), &placement), 0);
    laplace_ordered_composition_result_t result{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), &value, 1, &result), 0);
    EXPECT_EQ(result.tier, 0u);
    EXPECT_TRUE(hash128_equals(&result.id, &floor->hash));
    EXPECT_EQ(std::memcmp(result.coord, floor->coord, sizeof(result.coord)), 0);
    EXPECT_EQ(std::memcmp(&result.hilbert, &floor->hilbert, sizeof(result.hilbert)), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 0u);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 1u);
    EXPECT_EQ(result.first_physicality_row, 0u);
    expect_physicality(stage.get(), value, result);

    // Resolve-only singleton semantics continue to return the supplied child;
    // copying an admitted floor observation is specific to stage_batch.
    atom.coord[0] += 0.25;
    laplace_ordered_composition_result_t composed{};
    ASSERT_EQ(laplace_ordered_composition_compose_batch(&value, 1, &composed), 0);
    EXPECT_TRUE(hash128_equals(&composed.id, &atom.id));
    EXPECT_EQ(std::memcmp(composed.coord, atom.coord, sizeof(atom.coord)), 0);
    EXPECT_EQ(composed.emitted_physicality_rows, 0u);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 1u);
}

TEST(OrderedCompositionStage, MixedFloorObservationsAndReplayedCompositionsReportExactSpans) {
    laplace_ordered_component_t atoms[2]{};
    for (size_t i = 0; i < 2; ++i) {
        atoms[i].atom = static_cast<uint32_t>('a' + i);
        atoms[i].has_atom = 1;
        hilbert128_t ignored{};
        ASSERT_EQ(codepoint_table_resolve_atom(atoms[i].atom, &atoms[i].id, atoms[i].coord, &ignored), 0);
    }
    auto initial = request(atoms, 2);
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    laplace_ordered_composition_result_t first{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), &initial, 1, &first), 0);
    const auto opaque = component("already-realized-higher-tier-child", 4, 0.0);
    laplace_ordered_composition_request_t requests[] = {
        request(&atoms[0], 1), request(&opaque, 1), initial,
        request(&atoms[0], 1), request(&atoms[1], 1)};
    requests[0].source_id = hash("test/ordered/source-a");
    requests[3].source_id = hash("test/ordered/source-b");
    for (size_t i = 0; i < 5; ++i) requests[i].observed_at_unix_us += static_cast<int64_t>(10 + i);
    laplace_ordered_composition_result_t results[5]{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), requests, 5, results), 0);
    ASSERT_EQ(intent_stage_entity_count(stage.get()), 1u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), 5u);
    // New floor observations retain request order; the already witnessed
    // compositional candidate follows the previous winner-first pass.
    EXPECT_EQ(results[0].first_physicality_row, 1u);
    EXPECT_EQ(results[2].first_physicality_row, 4u);
    EXPECT_EQ(results[3].first_physicality_row, 2u);
    EXPECT_EQ(results[4].first_physicality_row, 3u);
    EXPECT_EQ(results[1].emitted_physicality_rows, 0u);
    EXPECT_TRUE(hash128_equals(&results[1].id, &opaque.id));
    EXPECT_EQ(results[1].tier, opaque.tier);
    expect_physicality(stage.get(), initial, first);
    for (size_t i : {0u, 2u, 3u, 4u}) expect_physicality(stage.get(), requests[i], results[i]);

    physicality_descriptor_vocabulary_t* raw_vocabulary = nullptr;
    ASSERT_EQ(physicality_descriptor_vocabulary_create(&initial.source_id, kDescriptorBudget,
        &raw_vocabulary), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_vocabulary_t, decltype(&physicality_descriptor_vocabulary_free)>
        vocabulary(raw_vocabulary, physicality_descriptor_vocabulary_free);
    const intent_stage_t* source_stage = stage.get();
    const physicality_descriptor_limits_t limits{kDescriptorBudget};
    physicality_descriptor_capture_t* raw_capture = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages(&source_stage, 1,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits,
        kDescriptorBudget, &raw_capture), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>
        capture(raw_capture, physicality_descriptor_capture_free);
    size_t count = 0;
    const auto* roots = physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(capture.get()), &count);
    ASSERT_EQ(count, 5u);
    EXPECT_TRUE(hash128_equals(&roots[0], &roots[4]));
    EXPECT_TRUE(hash128_equals(&roots[1], &roots[2]));
    EXPECT_FALSE(hash128_equals(&roots[1], &roots[3]));
    const auto* observations = physicality_descriptor_capture_observations(capture.get(), &count);
    ASSERT_EQ(count, 5u);
    for (size_t i : {0u, 2u, 3u, 4u}) {
        const size_t row = results[i].first_physicality_row;
        EXPECT_EQ(observations[row].source_stage_index, 0u);
        EXPECT_EQ(observations[row].source_row_index, row);
        EXPECT_EQ(observations[row].observed_at_unix_us, requests[i].observed_at_unix_us);
    }
}

TEST(OrderedCompositionStage, InvalidFloorSingletonPreventsEveryBatchAppend) {
    laplace_ordered_component_t atom{};
    atom.atom = 'a';
    atom.has_atom = 1;
    hilbert128_t ignored{};
    ASSERT_EQ(codepoint_table_resolve_atom(atom.atom, &atom.id, atom.coord, &ignored), 0);
    const laplace_ordered_component_t children[] = {
        component("prior-left", 1, 0.0), component("prior-right", 2, 1.0)};
    const auto valid = request(children, 2);
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    laplace_ordered_composition_result_t first{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), &valid, 1, &first), 0);
    size_t entity_size = 0, physicality_size = 0;
    const auto* entities = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ENTITIES, &entity_size);
    const auto* physicalities = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &physicality_size);
    const std::vector<uint8_t> before_entities(entities, entities + entity_size);
    const std::vector<uint8_t> before_physicalities(physicalities, physicalities + physicality_size);
    for (int fault = 0; fault < 4; ++fault) {
        SCOPED_TRACE(fault);
        auto invalid = atom;
        if (fault == 0) invalid.id = hash("not-the-declared-floor-atom");
        if (fault == 1) invalid.coord[0] += 0.25;
        if (fault == 2) invalid.atom = UINT32_MAX;
        if (fault == 3) invalid.has_atom = 0;
        const laplace_ordered_composition_request_t requests[] = {valid, request(&invalid, 1)};
        laplace_ordered_composition_result_t results[2]{};
        EXPECT_NE(laplace_ordered_composition_stage_batch(stage.get(), requests, 2, results), 0);
        EXPECT_EQ(intent_stage_entity_count(stage.get()), 1u);
        EXPECT_EQ(intent_stage_physicality_count(stage.get()), 1u);
        entities = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ENTITIES, &entity_size);
        physicalities = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &physicality_size);
        ASSERT_EQ(entity_size, before_entities.size());
        ASSERT_EQ(physicality_size, before_physicalities.size());
        EXPECT_EQ(std::memcmp(entities, before_entities.data(), entity_size), 0);
        EXPECT_EQ(std::memcmp(physicalities, before_physicalities.data(), physicality_size), 0);
    }
}

TEST(OrderedCompositionStage, MissingFloorRejectsMixedBatchBeforeAnyRow) {
    laplace_ordered_component_t atom{};
    atom.atom = 'a';
    atom.has_atom = 1;
    hilbert128_t ignored{};
    ASSERT_EQ(codepoint_table_resolve_atom(atom.atom, &atom.id, atom.coord, &ignored), 0);
    const laplace_ordered_component_t children[] = {
        component("left", 1, 0.0), component("right", 2, 1.0)};
    const laplace_ordered_composition_request_t requests[] = {request(children, 2), request(&atom, 1)};
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    struct RestoreFloor {
        ~RestoreFloor() { EXPECT_EQ(codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS), 0); }
    } restore;
    codepoint_table_unload();
    laplace_ordered_composition_result_t results[2]{};
    EXPECT_NE(laplace_ordered_composition_stage_batch(stage.get(), requests, 2, results), 0);
    EXPECT_EQ(intent_stage_entity_count(stage.get()), 0u);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 0u);
}

TEST(OrderedCompositionStage, HigherTierSingletonRequiresItsActualChildBodyFromProvider) {
    const hash128_t source = hash("test/ordered/source");
    tier_tree_t* raw_tree = nullptr;
    ASSERT_EQ(content_witness_tree_build(reinterpret_cast<const uint8_t*>("cd"), 2, &raw_tree), 0);
    std::unique_ptr<tier_tree_t, decltype(&tier_tree_free)> tree(raw_tree, tier_tree_free);
    tier_node_view_t child_root{};
    ASSERT_EQ(content_witness_tree_root_node(tree.get(), &child_root), 0);
    ASSERT_GT(child_root.tier, 0u);
    laplace_ordered_component_t children[2]{};
    children[0].id = child_root.id;
    children[0].tier = child_root.tier;
    std::memcpy(children[0].coord, child_root.coord, sizeof(child_root.coord));
    children[1].atom = 'a';
    children[1].has_atom = 1;
    hilbert128_t ignored{};
    ASSERT_EQ(codepoint_table_resolve_atom('a', &children[1].id, children[1].coord, &ignored), 0);
    const laplace_ordered_composition_request_t requests[] = {request(children, 1), request(children, 2)};
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    laplace_ordered_composition_result_t results[2]{};
    ASSERT_EQ(laplace_ordered_composition_stage_batch(stage.get(), requests, 2, results), 0);
    EXPECT_EQ(results[0].emitted_physicality_rows, 0u);
    EXPECT_TRUE(hash128_equals(&results[0].id, &child_root.id));
    ASSERT_EQ(intent_stage_entity_count(stage.get()), 1u);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), 1u);
    expect_physicality(stage.get(), requests[1], results[1]);

    physicality_descriptor_vocabulary_t* raw_vocabulary = nullptr;
    ASSERT_EQ(physicality_descriptor_vocabulary_create(&source, kDescriptorBudget, &raw_vocabulary),
        PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_vocabulary_t, decltype(&physicality_descriptor_vocabulary_free)>
        vocabulary(raw_vocabulary, physicality_descriptor_vocabulary_free);
    const intent_stage_t* source_stage = stage.get();
    const physicality_descriptor_limits_t limits{kDescriptorBudget};
    physicality_descriptor_capture_t* raw_capture = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages(&source_stage, 1,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits,
        kDescriptorBudget, &raw_capture), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>
        capture(raw_capture, physicality_descriptor_capture_free);
    const physicality_descriptor_source_observation_t witness{source, hash("test/ordered/source-unit"), 0.8};
    physicality_descriptor_materialization_t* raw_materialized = nullptr;
    ASSERT_EQ(physicality_descriptor_materialize(capture.get(), vocabulary.get(),
        nullptr, 0, nullptr, 0, nullptr, 0, &witness, 1, &source,
        requests[1].observed_at_unix_us, kDescriptorBudget, &raw_materialized),
        PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    std::unique_ptr<physicality_descriptor_materialization_t, decltype(&physicality_descriptor_materialization_free)>
        materialized(raw_materialized, physicality_descriptor_materialization_free);
    size_t count = 0;
    const auto* pending = physicality_descriptor_materialization_pending(materialized.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_TRUE(hash128_equals(&pending[0], &child_root.id));
    EXPECT_EQ(physicality_descriptor_materialization_take_stage(materialized.get()), nullptr);

    Stage child_stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(child_stage, nullptr);
    hash128_t emitted_child{};
    ASSERT_EQ(content_witness_emit_tree(child_stage.get(), tree.get(), &source,
        nullptr, 0, &emitted_child), 0);
    EXPECT_TRUE(hash128_equals(&emitted_child, &child_root.id));
    ASSERT_EQ(intent_stage_physicality_count(child_stage.get()), 1u);
    const intent_stage_t* provider = child_stage.get();
    raw_materialized = nullptr;
    ASSERT_EQ(physicality_descriptor_materialize(capture.get(), vocabulary.get(),
        &provider, 1, nullptr, 0, nullptr, 0, &witness, 1, &source,
        requests[1].observed_at_unix_us, kDescriptorBudget, &raw_materialized), PHYSICALITY_DESCRIPTOR_OK);
    materialized.reset(raw_materialized);
    Stage generated(physicality_descriptor_materialization_take_stage(materialized.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    EXPECT_EQ(intent_stage_attestation_count(generated.get()), 1u);
}

}  // namespace
