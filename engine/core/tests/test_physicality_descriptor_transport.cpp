#include <gtest/gtest.h>

#include <array>
#include <cstring>
#include <memory>
#include <vector>

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/trajectory.h"

namespace {
using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
constexpr size_t kBudget = 1024u * 1024u;
const hash128_t kEntity{123u, 456u};

Stage sample_stage() {
    Stage stage(intent_stage_new(0u), intent_stage_free);
    const hash128_t type = laplace_content_tier_type_id(4);
    EXPECT_EQ(intent_stage_add_entity(stage.get(), &kEntity, 4, &type, &kEntity), 0);
    hash128_t placement;
    laplace_physicality_id_compute(kEntity, 3, &placement);
    const double coord[4] = {0.125, 0.25, 0.375, 0.5};
    const hilbert128_t hb{};
    EXPECT_EQ(intent_stage_add_physicality(stage.get(), &placement, &kEntity, 3,
        coord, &hb, nullptr, 0u, 0, 1, 0.0, 1, 0, 42), 0);
    EXPECT_EQ(intent_stage_add_attestation(stage.get(), &kEntity, &kEntity, &type,
        nullptr, &kEntity, nullptr, 2, 42, 1, 1000000000, 350000000000,
        1500000000000, nullptr), 0);
    return stage;
}

std::vector<uint8_t> tuples(const intent_stage_t* stage, intent_stage_table_t table) {
    size_t size = 0u;
    const auto* bytes = intent_stage_tuple_ptr(stage, table, &size);
    return std::vector<uint8_t>(bytes, bytes + size);
}

TEST(PhysicalityDescriptorTransport, TupleImportPreservesAllThreeAuthoritativeNativeRows) {
    const auto original = sample_stage();
    const auto entities = tuples(original.get(), INTENT_STAGE_TABLE_ENTITIES);
    const auto physicalities = tuples(original.get(), INTENT_STAGE_TABLE_PHYSICALITIES);
    const auto attestations = tuples(original.get(), INTENT_STAGE_TABLE_ATTESTATIONS);
    intent_stage_t* raw = nullptr;
    ASSERT_EQ(intent_stage_from_tuple_bytes(entities.data(), entities.size(),
        physicalities.data(), physicalities.size(), attestations.data(), attestations.size(),
        kBudget, &raw), 0);
    const Stage imported(raw, intent_stage_free);
    EXPECT_EQ(intent_stage_entity_count(imported.get()), 1u);
    EXPECT_EQ(intent_stage_physicality_count(imported.get()), 1u);
    EXPECT_EQ(intent_stage_attestation_count(imported.get()), 1u);
    EXPECT_EQ(tuples(imported.get(), INTENT_STAGE_TABLE_ENTITIES), entities);
    EXPECT_EQ(tuples(imported.get(), INTENT_STAGE_TABLE_PHYSICALITIES), physicalities);
    EXPECT_EQ(tuples(imported.get(), INTENT_STAGE_TABLE_ATTESTATIONS), attestations);
    EXPECT_LE(intent_stage_memory_bytes(imported.get()), kBudget);
}

TEST(PhysicalityDescriptorTransport, RejectsTruncatedWrongColumnAndInvalidNegativeLengths) {
    const auto original = sample_stage();
    const auto valid = tuples(original.get(), INTENT_STAGE_TABLE_PHYSICALITIES);
    for (size_t count = 1u; count < valid.size(); ++count) {
        intent_stage_t* raw = nullptr;
        EXPECT_EQ(intent_stage_from_tuple_bytes(nullptr, 0u, valid.data(), count,
            nullptr, 0u, kBudget, &raw), -1) << count;
        EXPECT_EQ(raw, nullptr);
    }
    auto invalid = valid;
    invalid[1] = 9;
    intent_stage_t* raw = nullptr;
    EXPECT_EQ(intent_stage_from_tuple_bytes(nullptr, 0u, invalid.data(), invalid.size(),
        nullptr, 0u, kBudget, &raw), -1);
    invalid = valid;
    invalid[2] = 255; invalid[3] = 255; invalid[4] = 255; invalid[5] = 254;
    EXPECT_EQ(intent_stage_from_tuple_bytes(nullptr, 0u, invalid.data(), invalid.size(),
        nullptr, 0u, kBudget, &raw), -1);
    EXPECT_EQ(raw, nullptr);
}

TEST(PhysicalityDescriptorTransport, ImportRejectsBudgetBeforePublishingPartialStage) {
    const auto original = sample_stage();
    const auto valid = tuples(original.get(), INTENT_STAGE_TABLE_PHYSICALITIES);
    const Stage empty(intent_stage_new(0u), intent_stage_free);
    intent_stage_t* raw = nullptr;
    EXPECT_EQ(intent_stage_from_tuple_bytes(nullptr, 0u, valid.data(), valid.size(),
        nullptr, 0u, intent_stage_memory_bytes(empty.get()), &raw), -2);
    EXPECT_EQ(raw, nullptr);
}

TEST(PhysicalityDescriptorTransport, LogicalPreflightCountsRunsWithoutExpandingThem) {
    Stage stage(intent_stage_new(0u), intent_stage_free);
    hash128_t placement;
    double trajectory[4];
    const mantissa_payload_t carrier{kEntity, 1u, 65535u, 0u};
    mantissa_pack(trajectory, &carrier);
    hash128_t entity;
    size_t expanded;
    ASSERT_EQ(trajectory_content_identity(trajectory, 1u, &entity, &expanded), 0);
    laplace_physicality_id_compute(entity, 1, &placement);
    const double coord[4] = {0.125, 0.25, 0.375, 0.5};
    const hilbert128_t hb{};
    ASSERT_EQ(intent_stage_add_physicality(stage.get(), &placement, &entity, 1,
        coord, &hb, trajectory, 1u, 65535, 1, 0.0, 1, 0, 42), 0);
    const intent_stage_t* stages[] = {stage.get()};
    size_t bodies = 99, vertices = 99, logical = 99;
    EXPECT_EQ(physicality_descriptor_stages_preflight(stages, 1u, 65534u,
        &bodies, &vertices, &logical), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(bodies, 99u); EXPECT_EQ(vertices, 99u); EXPECT_EQ(logical, 99u);
    ASSERT_EQ(physicality_descriptor_stages_preflight(stages, 1u, 65535u,
        &bodies, &vertices, &logical), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(bodies, 1u); EXPECT_EQ(vertices, 1u); EXPECT_EQ(logical, 65535u);
    const intent_stage_t* twice[] = {stage.get(), stage.get()};
    EXPECT_EQ(physicality_descriptor_stages_preflight(twice, 2u, 65535u,
        nullptr, nullptr, nullptr), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(physicality_descriptor_stages_preflight(twice, 2u, 131070u,
        &bodies, &vertices, &logical), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(bodies, 2u); EXPECT_EQ(vertices, 2u); EXPECT_EQ(logical, 131070u);
}

TEST(PhysicalityDescriptorTransport, FactorFloatAndTestimonyGamesAreNotOrdinaryRunLengths) {
    const float arena = 140.0f;
    const float factors[] = {0.25f, -0.75f, 0.5f, 0.125f, 0.875f, -0.625f, 0.0625f};
    double trajectory[16]{};
    size_t vertices = 0u;
    ASSERT_EQ(laplace_factor_pack_values(&arena, 1u, trajectory, &vertices), 0);
    ASSERT_EQ(vertices, 1u);
    const int64_t score = 500000000;
    const uint16_t dimensions = 7;
    ASSERT_EQ(laplace_testimony_pack_walk(&kEntity, &score, &dimensions, 1u, trajectory + 4), 0);
    ASSERT_EQ(laplace_factor_pack_values(factors, 7u, trajectory + 8, &vertices), 0);
    ASSERT_EQ(vertices, 2u);
    size_t ordinary = 99u;
    int typed = 0;
    ASSERT_EQ(trajectory_manifest_scan(trajectory, 4u, &ordinary, &typed), 0);
    EXPECT_EQ(typed, 1); EXPECT_EQ(ordinary, 0u);
    EXPECT_EQ(laplace_physicality_manifest_validate(&kEntity, 3, trajectory, 4u, 1), 0);
    EXPECT_EQ(laplace_physicality_manifest_validate(&kEntity, 1, trajectory, 4u, 1), -3);

    Stage stage(intent_stage_new_bounded(0u, kBudget), intent_stage_free);
    hash128_t placement;
    laplace_physicality_id_compute(kEntity, 3, &placement);
    const double coord[4] = {0.125, 0.25, 0.375, 0.5};
    const hilbert128_t hb{};
    ASSERT_EQ(intent_stage_add_physicality(stage.get(), &placement, &kEntity, 3,
        coord, &hb, trajectory, 4u, 1, 1, 0.0, 0, 7, 42), 0);
    const intent_stage_t* stages[] = {stage.get()};
    size_t bodies = 0u, stored = 0u, logical = 99u;
    ASSERT_EQ(physicality_descriptor_stages_preflight(stages, 1u, 0u,
        &bodies, &stored, &logical), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(bodies, 1u); EXPECT_EQ(stored, 4u); EXPECT_EQ(logical, 0u);
}

TEST(PhysicalityDescriptorTransport, RawBatchRejectsClaimedPlacementWithoutRewritingIt) {
    Stage stage(intent_stage_new_bounded(0u, kBudget), intent_stage_free);
    physicality_descriptor_input_t input{};
    input.entity_id = kEntity;
    input.type = 3;
    input.alignment_residual_is_null = 1;
    input.source_dim_is_null = 1;
    const std::array<physicality_descriptor_input_t,2> inputs{input, input};
    const int64_t times[] = {41, 42};
    std::array<hash128_t,2> claimed{};
    laplace_physicality_id_compute(kEntity, 3, &claimed[0]);
    claimed[1] = claimed[0]; claimed[1].lo ^= 1u;
    EXPECT_EQ(physicality_descriptor_stage_add_batch(stage.get(), inputs.data(), claimed.data(), times, 2u),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 1u);
    const Stage rejected(intent_stage_new_bounded(0u, kBudget), intent_stage_free);
    EXPECT_EQ(physicality_descriptor_stage_add_batch(rejected.get(), &input, &claimed[1], times, 1u),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(intent_stage_physicality_count(rejected.get()), 0u);
}
}
