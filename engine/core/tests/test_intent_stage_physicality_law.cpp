#include <gtest/gtest.h>

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/intent_stage.h"
#include "laplace/core/trajectory.h"

#include <array>

namespace {

hash128_t h(uint8_t seed) {
    hash128_t out{};
    for (int i = 0; i < 16; ++i)
        reinterpret_cast<uint8_t*>(&out)[i] = static_cast<uint8_t>(seed + i);
    return out;
}

hilbert128_t hb() {
    hilbert128_t value{};
    return value;
}

double coord[4] = {1.0, 0.0, 0.0, 0.0};

}  // namespace

TEST(IntentStagePhysicalityLaw, AcceptsExactMultiChildContentManifest) {
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(stage, nullptr);

    std::array<hash128_t, 2> children = {h(0x10), h(0x30)};
    hash128_t entity{};
    hash128_merkle(0, children.data(), children.size(), &entity);
    hash128_t physicality{};
    laplace_physicality_id_compute(entity, 1, &physicality);
    double trajectory[8]{};
    ASSERT_EQ(trajectory_build(children.data(), children.size(), trajectory), 0);
    auto hilbert = hb();

    EXPECT_EQ(intent_stage_add_physicality(
        stage, &physicality, &entity, 1, coord, &hilbert,
        trajectory, 2, 2, 1, 0.0, 1, 0, 0), 0);
    EXPECT_EQ(intent_stage_physicality_count(stage), 1u);
    intent_stage_free(stage);
}

TEST(IntentStagePhysicalityLaw, AcceptsSingleChildCollapse) {
    intent_stage_t* stage = intent_stage_new(2);
    ASSERT_NE(stage, nullptr);

    hash128_t entity = h(0x20);
    hash128_t physicality{};
    laplace_physicality_id_compute(entity, 1, &physicality);
    double trajectory[4]{};
    ASSERT_EQ(trajectory_build(&entity, 1, trajectory), 0);
    auto hilbert = hb();

    EXPECT_EQ(intent_stage_add_physicality(
        stage, &physicality, &entity, 1, coord, &hilbert,
        trajectory, 1, 1, 1, 0.0, 1, 0, 0), 0);
    intent_stage_free(stage);
}

TEST(IntentStagePhysicalityLaw, RejectsContentParentThatDoesNotMatchManifest) {
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(stage, nullptr);

    std::array<hash128_t, 2> children = {h(0x10), h(0x30)};
    hash128_t false_parent = h(0x70);
    hash128_t physicality{};
    laplace_physicality_id_compute(false_parent, 1, &physicality);
    double trajectory[8]{};
    ASSERT_EQ(trajectory_build(children.data(), children.size(), trajectory), 0);
    auto hilbert = hb();

    EXPECT_EQ(intent_stage_add_physicality(
        stage, &physicality, &false_parent, 1, coord, &hilbert,
        trajectory, 2, 2, 1, 0.0, 1, 0, 0), -4);
    EXPECT_EQ(intent_stage_physicality_count(stage), 0u);
    intent_stage_free(stage);
}

TEST(IntentStagePhysicalityLaw, RejectsWrongLogicalConstituentCount) {
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(stage, nullptr);

    hash128_t child = h(0x40);
    std::array<hash128_t, 3> children = {child, child, child};
    hash128_t entity{};
    hash128_merkle(0, children.data(), children.size(), &entity);
    hash128_t physicality{};
    laplace_physicality_id_compute(entity, 1, &physicality);
    double trajectory[12]{};
    size_t vertices = 0;
    ASSERT_EQ(trajectory_build_rle(
        children.data(), children.size(), trajectory, &vertices), 0);
    ASSERT_LT(vertices, children.size());
    auto hilbert = hb();

    EXPECT_EQ(intent_stage_add_physicality(
        stage, &physicality, &entity, 1, coord, &hilbert,
        trajectory, static_cast<uint32_t>(vertices), 2,
        1, 0.0, 1, 0, 0), -3);
    intent_stage_free(stage);
}

TEST(IntentStagePhysicalityLaw, StructuralTrajectoryMayProjectGovernedIdentity) {
    intent_stage_t* stage = intent_stage_new(4);
    ASSERT_NE(stage, nullptr);

    std::array<hash128_t, 2> members = {h(0x10), h(0x30)};
    hash128_t governed = h(0x70);
    hash128_t physicality{};
    laplace_physicality_id_compute(governed, 8, &physicality);
    double trajectory[8]{};
    ASSERT_EQ(trajectory_build(members.data(), members.size(), trajectory), 0);
    auto hilbert = hb();

    EXPECT_EQ(intent_stage_add_physicality(
        stage, &physicality, &governed, 8, coord, &hilbert,
        trajectory, 2, 2, 1, 0.0, 1, 0, 0), 0);
    intent_stage_free(stage);
}

TEST(IntentStagePhysicalityLaw, RejectsWrongPhysicalityId) {
    intent_stage_t* stage = intent_stage_new(2);
    ASSERT_NE(stage, nullptr);

    hash128_t entity = h(0x20);
    hash128_t wrong = h(0x90);
    auto hilbert = hb();

    EXPECT_EQ(intent_stage_add_physicality(
        stage, &wrong, &entity, 3, coord, &hilbert,
        nullptr, 0, 0, 1, 0.0, 1, 0, 0), -2);
    EXPECT_EQ(intent_stage_physicality_count(stage), 0u);
    intent_stage_free(stage);
}
