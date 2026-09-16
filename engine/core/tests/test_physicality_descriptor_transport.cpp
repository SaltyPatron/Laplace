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

TEST(PhysicalityDescriptorTransport, RetainingPhysicalitiesReleasesExactCapacitiesAndWitnessCache) {
    auto original = sample_stage();
    const auto entities = tuples(original.get(), INTENT_STAGE_TABLE_ENTITIES);
    const auto physicalities = tuples(original.get(), INTENT_STAGE_TABLE_PHYSICALITIES);
    const auto attestations = tuples(original.get(), INTENT_STAGE_TABLE_ATTESTATIONS);
    intent_stage_t* raw = nullptr;
    ASSERT_EQ(intent_stage_from_tuple_bytes(entities.data(), entities.size(),
        physicalities.data(), physicalities.size(), attestations.data(), attestations.size(),
        4u * kBudget, &raw), 0);
    Stage imported(raw, intent_stage_free);
    raw = nullptr;
    ASSERT_EQ(intent_stage_from_tuple_bytes(nullptr, 0u, physicalities.data(), physicalities.size(),
        nullptr, 0u, 4u * kBudget, &raw), 0);
    const Stage only_physicalities(raw, intent_stage_free);
    const size_t before_witness = intent_stage_memory_bytes(imported.get());
    ASSERT_EQ(intent_stage_witness_record(imported.get(), &kEntity), 0);
    ASSERT_EQ(intent_stage_witness_seen(imported.get(), &kEntity), 1);
    const size_t before = intent_stage_memory_bytes(imported.get());
    const size_t witness_bytes = before - before_witness;
    ASSERT_GT(witness_bytes, 0u);
    const size_t peak = intent_stage_memory_peak_bytes(imported.get());
    const size_t expected_retained = intent_stage_memory_bytes(only_physicalities.get());
    const size_t expected_released = before - expected_retained;
    EXPECT_GE(expected_released, witness_bytes + entities.size() + attestations.size());
    size_t physicality_bytes = 0;
    const auto* borrowed = intent_stage_tuple_ptr(imported.get(), INTENT_STAGE_TABLE_PHYSICALITIES,
        &physicality_bytes);
    original.reset();

    EXPECT_EQ(intent_stage_retain_physicalities(nullptr), 0u);
    EXPECT_EQ(intent_stage_retain_physicalities(imported.get()), expected_released);
    EXPECT_EQ(intent_stage_memory_bytes(imported.get()), expected_retained);
    EXPECT_EQ(intent_stage_memory_peak_bytes(imported.get()), peak);
    EXPECT_EQ(intent_stage_entity_count(imported.get()), 0u);
    EXPECT_EQ(intent_stage_attestation_count(imported.get()), 0u);
    EXPECT_EQ(intent_stage_physicality_count(imported.get()), 1u);
    EXPECT_EQ(intent_stage_witness_seen(imported.get(), &kEntity), 0);
    for (const auto table : {INTENT_STAGE_TABLE_ENTITIES, INTENT_STAGE_TABLE_ATTESTATIONS}) {
        size_t discarded_bytes = 1u;
        EXPECT_EQ(intent_stage_tuple_ptr(imported.get(), table, &discarded_bytes), nullptr);
        EXPECT_EQ(discarded_bytes, 0u);
    }
    size_t retained_bytes = 0;
    EXPECT_EQ(intent_stage_tuple_ptr(imported.get(), INTENT_STAGE_TABLE_PHYSICALITIES,
        &retained_bytes), borrowed);
    EXPECT_EQ(retained_bytes, physicality_bytes);
    EXPECT_EQ(tuples(imported.get(), INTENT_STAGE_TABLE_PHYSICALITIES), physicalities);
    EXPECT_EQ(intent_stage_retain_physicalities(imported.get()), 0u);
    EXPECT_EQ(intent_stage_memory_bytes(imported.get()), expected_retained);
    EXPECT_EQ(intent_stage_memory_peak_bytes(imported.get()), peak);

    const intent_stage_t* stages[]{imported.get()};
    physicality_descriptor_capture_t* captured_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stage_rows(stages, 1u, kBudget, &captured_raw),
        PHYSICALITY_DESCRIPTOR_OK);
    const std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>
        captured(captured_raw, physicality_descriptor_capture_free);
    size_t count = 0;
    const auto* observations = physicality_descriptor_capture_observations(captured.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_EQ(observations[0].observed_at_unix_us, 42);
    EXPECT_EQ(observations[0].source_stage_index, 0u);
    EXPECT_EQ(observations[0].source_row_index, 0u);
    // A later owner cannot inherit stale entity-witness membership.
    EXPECT_EQ(intent_stage_witness_record(imported.get(), &kEntity), 0);
    EXPECT_EQ(intent_stage_witness_seen(imported.get(), &kEntity), 1);
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

TEST(PhysicalityDescriptorTransport, SingleStoredRunUsesPointAndRejectsOneVertexLineString) {
    const hash128_t children[] = {kEntity, kEntity};
    double packed[8];
    size_t stored = 0;
    ASSERT_EQ(trajectory_build_rle(children, 2, packed, &stored), 0);
    ASSERT_EQ(stored, 1u);
    hash128_t entity, placement;
    size_t logical = 0;
    ASSERT_EQ(trajectory_content_identity(packed, stored, &entity, &logical), 0);
    ASSERT_EQ(logical, 2u);
    laplace_physicality_id_compute(entity, 1, &placement);
    Stage original(intent_stage_new(1), intent_stage_free);
    const double coordinate[4] = {0.125, 0.25, 0.375, 0.5};
    hilbert128_t hilbert;
    hilbert4d_encode(coordinate, &hilbert);
    ASSERT_EQ(intent_stage_add_physicality(original.get(), &placement, &entity, 1,
        coordinate, &hilbert, packed, 1, 2, 1, 0.0, 1, 0, 42), 0);
    const intent_stage_t* stages[] = {original.get()};
    size_t bodies = 0, vertices = 0;
    ASSERT_EQ(physicality_descriptor_stages_preflight(stages, 1, 2,
        &bodies, &vertices, &logical), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(bodies, 1u); EXPECT_EQ(vertices, 1u); EXPECT_EQ(logical, 2u);
    auto frame = tuples(original.get(), INTENT_STAGE_TABLE_PHYSICALITIES);
    size_t at = 2, trajectory_length_at = 0, trajectory_at = 0;
    for (unsigned column = 0; column <= 5; ++column) {
        const uint32_t length = (uint32_t(frame[at]) << 24u) | (uint32_t(frame[at+1]) << 16u) |
            (uint32_t(frame[at+2]) << 8u) | frame[at+3];
        if (column == 5) {
            trajectory_length_at = at; trajectory_at = at + 4;
            ASSERT_EQ(length, 37u);
        }
        at += 4;
        if (length != UINT32_MAX) at += length;
    }
    const std::array<uint8_t,5> point{{1,1,0,0,0xc0}};
    ASSERT_EQ(std::memcmp(frame.data()+trajectory_at, point.data(), point.size()), 0);
    frame[trajectory_length_at+3] = 41;
    frame[trajectory_at+1] = 2;
    frame.insert(frame.begin()+static_cast<std::ptrdiff_t>(trajectory_at+5), {1,0,0,0});
    intent_stage_t* raw = nullptr;
    ASSERT_EQ(intent_stage_from_tuple_bytes(nullptr, 0, frame.data(), frame.size(),
        nullptr, 0, kBudget, &raw), 0); // Framing is valid, typed geometry is not.
    Stage malformed(raw, intent_stage_free);
    stages[0] = malformed.get();
    EXPECT_EQ(physicality_descriptor_stages_preflight(stages, 1, 2,
        nullptr, nullptr, nullptr), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
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
TEST(PhysicalityDescriptorTransport, CopyGeometryRequiresEwkbRatherThanIsoWkb) {
    Stage stage(intent_stage_new(0u), intent_stage_free);
    const hash128_t children[] = {{1u, 2u}, {3u, 4u}};
    double trajectory[8]{};
    ASSERT_EQ(trajectory_build(children, 2u, trajectory), 0);
    hash128_t entity, placement;
    size_t logical = 0u;
    ASSERT_EQ(trajectory_content_identity(trajectory, 2u, &entity, &logical), 0);
    laplace_physicality_id_compute(entity, 1, &placement);
    const double coord[] = {0.125, 0.25, 0.375, 0.5};
    const hilbert128_t hb{};
    ASSERT_EQ(intent_stage_add_physicality(stage.get(), &placement, &entity, 1,
        coord, &hb, trajectory, 2u, 2, 1, 0.0, 1, 0, 42), 0);
    const intent_stage_t* stages[] = {stage.get()};
    size_t bodies = 0, stored = 0, work = 0;
    ASSERT_EQ(physicality_descriptor_stages_preflight(stages, 1u, 2u,
        &bodies, &stored, &work), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(bodies, 1u); EXPECT_EQ(stored, 2u); EXPECT_EQ(work, 2u);
    const auto valid = tuples(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES);
    for (size_t field : {3u, 5u}) {
        auto iso = valid;
        size_t cursor = 2u;
        for (size_t i = 0; i <= field; ++i) {
            ASSERT_LE(cursor + 4u, iso.size());
            const uint32_t length = (uint32_t(iso[cursor]) << 24u) |
                (uint32_t(iso[cursor + 1u]) << 16u) |
                (uint32_t(iso[cursor + 2u]) << 8u) | iso[cursor + 3u];
            cursor += 4u;
            if (i == field) break;
            ASSERT_NE(length, UINT32_MAX);
            cursor += length;
        }
        ASSERT_LE(cursor + 5u, iso.size());
        EXPECT_EQ(iso[cursor], 1u);
        const uint32_t ewkb = field == 3u ? UINT32_C(0xc0000001) : UINT32_C(0xc0000002);
        uint32_t actual = 0; std::memcpy(&actual, iso.data() + cursor + 1u, sizeof(actual));
        ASSERT_EQ(actual, ewkb);
        // These are the ISO ZM tags produced by PostGIS ST_AsBinary. Tuple
        // framing remains valid, but this is not the native COPY geometry law.
        const uint32_t iso_type = field == 3u ? 3001u : 3002u;
        std::memcpy(iso.data() + cursor + 1u, &iso_type, sizeof(iso_type));
        intent_stage_t* raw = nullptr;
        ASSERT_EQ(intent_stage_from_tuple_bytes(nullptr, 0u, iso.data(), iso.size(),
            nullptr, 0u, kBudget, &raw), 0);
        Stage imported(raw, intent_stage_free);
        const intent_stage_t* invalid[] = {imported.get()};
        bodies = stored = work = 99u;
        EXPECT_EQ(physicality_descriptor_stages_preflight(invalid, 1u, 2u,
            &bodies, &stored, &work), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
        EXPECT_EQ(bodies, 99u); EXPECT_EQ(stored, 99u); EXPECT_EQ(work, 99u);
    }
}

TEST(PhysicalityDescriptorTransport, PlanFreeExportOwnsExactBodiesAndObservationOrder) {
    auto first = sample_stage();
    Stage second(intent_stage_new(0u), intent_stage_free);
    hash128_t placement;
    laplace_physicality_id_compute(kEntity, 3, &placement);
    const hash128_t children[] = {kEntity, {777u, 888u}};
    double trajectory[8];
    ASSERT_EQ(trajectory_build(children, 2u, trajectory), 0);
    const double coord[4] = {0.25, 0.125, 0.5, 0.375};
    const hilbert128_t hb{};
    ASSERT_EQ(intent_stage_add_physicality(second.get(), &placement, &kEntity, 3,
        coord, &hb, trajectory, 2u, 2, 0, 0.125, 0, 4, 43), 0);
    const intent_stage_t* stages[] = {first.get(), second.get()};
    physicality_descriptor_capture_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stage_rows(stages, 2u, kBudget, &raw),
        PHYSICALITY_DESCRIPTOR_OK);
    const std::unique_ptr<physicality_descriptor_capture_t,
        decltype(&physicality_descriptor_capture_free)> capture(raw, physicality_descriptor_capture_free);
    ASSERT_EQ(physicality_descriptor_capture_plan(capture.get()), nullptr);
    EXPECT_LE(physicality_descriptor_capture_bytes(capture.get()), kBudget);
    // The capture owns its decoded payload after both native stages are retired.
    first.reset(); second.reset();
    size_t count = 0u, observation_count = 0u;
    const auto* inputs = physicality_descriptor_capture_inputs(capture.get(), &count);
    const auto* observations = physicality_descriptor_capture_observations(capture.get(), &observation_count);
    ASSERT_EQ(count, 2u); ASSERT_EQ(observation_count, 2u);
    EXPECT_TRUE(hash128_equals(&inputs[0].entity_id, &kEntity));
    EXPECT_TRUE(hash128_equals(&inputs[1].entity_id, &kEntity));
    EXPECT_TRUE(hash128_equals(&observations[0].placement_id, &observations[1].placement_id));
    EXPECT_EQ(observations[0].source_stage_index, 0u);
    EXPECT_EQ(observations[1].source_stage_index, 1u);
    EXPECT_EQ(observations[0].source_row_index, 0u);
    EXPECT_EQ(observations[1].source_row_index, 0u);
    EXPECT_EQ(observations[0].observed_at_unix_us, 42);
    EXPECT_EQ(observations[1].observed_at_unix_us, 43);
    EXPECT_DOUBLE_EQ(inputs[0].coord[0], 0.125);
    EXPECT_EQ(inputs[0].trajectory_vertices, 0u);
    EXPECT_EQ(inputs[0].trajectory_xyzm, nullptr);
    EXPECT_EQ(inputs[0].alignment_residual_is_null, 1);
    EXPECT_EQ(inputs[0].source_dim_is_null, 1);
    EXPECT_DOUBLE_EQ(inputs[1].coord[0], 0.25);
    EXPECT_EQ(inputs[1].trajectory_vertices, 2u);
    EXPECT_EQ(inputs[1].n_constituents, 2);
    EXPECT_EQ(std::memcmp(inputs[1].trajectory_xyzm, trajectory, sizeof(trajectory)), 0);
    EXPECT_EQ(inputs[1].alignment_residual_is_null, 0);
    EXPECT_DOUBLE_EQ(inputs[1].alignment_residual, 0.125);
    EXPECT_EQ(inputs[1].source_dim_is_null, 0);
    EXPECT_EQ(inputs[1].source_dim, 4);
}

TEST(PhysicalityDescriptorTransport, PlanFreeExportRejectsBudgetFramingAndClaimedPlacement) {
    auto stage = sample_stage();
    const intent_stage_t* stages[] = {stage.get()};
    physicality_descriptor_capture_t* output = nullptr;
    EXPECT_EQ(physicality_descriptor_capture_stage_rows(stages, 1u, 1u, &output),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(output, nullptr);
    const intent_stage_t* missing[] = {nullptr};
    EXPECT_EQ(physicality_descriptor_capture_stage_rows(missing, 1u, kBudget, &output),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(output, nullptr);
    size_t length = 0u;
    auto* bytes = const_cast<uint8_t*>(intent_stage_tuple_ptr(
        stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &length));
    ASSERT_GT(length, 22u);
    bytes[1] = 9; // The declared ten-column native row is now malformed.
    EXPECT_EQ(physicality_descriptor_capture_stage_rows(stages, 1u, kBudget, &output),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
    bytes[1] = 10; bytes[6] ^= 1u; // Valid framing, counterfeit placement ID.
    EXPECT_EQ(physicality_descriptor_capture_stage_rows(stages, 1u, kBudget, &output),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
}

TEST(PhysicalityDescriptorTransport, PlanFreeExportRepresentsAnEmptyRowSetWithoutAPlan) {
    physicality_descriptor_capture_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stage_rows(nullptr, 0u, kBudget, &raw),
        PHYSICALITY_DESCRIPTOR_OK);
    const std::unique_ptr<physicality_descriptor_capture_t,
        decltype(&physicality_descriptor_capture_free)> capture(raw, physicality_descriptor_capture_free);
    size_t count = 99u;
    EXPECT_EQ(physicality_descriptor_capture_inputs(capture.get(), &count), nullptr);
    EXPECT_EQ(count, 0u);
    EXPECT_EQ(physicality_descriptor_capture_plan(capture.get()), nullptr);
}

}
