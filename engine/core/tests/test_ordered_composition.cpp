#include <gtest/gtest.h>

#include <cmath>
#include <cstring>

#include "laplace/core/hash_composer.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/ordered_composition.h"

namespace {

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

const uint8_t* field(const uint8_t* row, size_t row_len, uint16_t wanted,
                     uint32_t* out_len) {
    if (row_len < 2) return nullptr;
    const uint16_t columns = (uint16_t)((row[0] << 8) | row[1]);
    size_t offset = 2;
    for (uint16_t i = 0; i < columns; ++i) {
        if (offset + 4 > row_len) return nullptr;
        const int32_t length = (int32_t)be32(row + offset);
        offset += 4;
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
                       const laplace_ordered_component_t* children, size_t n) {
    size_t byte_count = 0;
    const uint8_t* rows = intent_stage_tuple_ptr(
        stage, INTENT_STAGE_TABLE_PHYSICALITIES, &byte_count);
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

TEST(OrderedCompositionStage, BatchDeduplicatesIdenticalParents) {
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
    EXPECT_EQ(1u, intent_stage_physicality_count(stage));
    intent_stage_free(stage);
}

TEST(OrderedCompositionStage, SameIdentityAtDifferentFloorsStagesTheMinimumFloor) {
    laplace_ordered_component_t higher[] = {
        component("left", 4, 0.0), component("right", 5, 1.0)};
    laplace_ordered_component_t lower[] = {
        component("left", 1, 0.0), component("right", 2, 1.0)};
    laplace_ordered_composition_request_t requests[] = {
        request(higher, 2), request(lower, 2)};
    laplace_ordered_composition_result_t results[2]{};
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(nullptr, stage);

    ASSERT_EQ(0, laplace_ordered_composition_stage_batch(stage, requests, 2, results));
    EXPECT_TRUE(hash128_equals(&results[0].id, &results[1].id));
    EXPECT_EQ(6, results[0].tier);
    EXPECT_EQ(3, results[1].tier);
    EXPECT_EQ(1u, intent_stage_entity_count(stage));
    EXPECT_EQ(3, only_entity_tier(stage));
    expect_trajectory(stage, lower, 2);
    intent_stage_free(stage);
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
    EXPECT_EQ(3, only_entity_tier(stage));
    intent_stage_free(stage);
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

}  // namespace
