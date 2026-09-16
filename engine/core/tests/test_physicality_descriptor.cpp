#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <set>
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

#include "laplace/core/mantissa.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/physicality_descriptor.h"
#include "laplace/core/trajectory.h"

namespace {

using Plan = std::unique_ptr<physicality_descriptor_plan_t,
    decltype(&physicality_descriptor_plan_free)>;
using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
using Capture = std::unique_ptr<physicality_descriptor_capture_t,
    decltype(&physicality_descriptor_capture_free)>;
using Readback = std::unique_ptr<physicality_descriptor_readback_t,
    decltype(&physicality_descriptor_readback_free)>;

/* Explicit controlled provider for representation-law tests. These values are
 * not an installed Unicode floor or evidence of persisted descriptor content.
 * Production obtains the same vocabulary through the ordinary content owner. */
physicality_descriptor_basis_t basis() {
    physicality_descriptor_basis_t result{};
    for (size_t i = 0; i < PHYSICALITY_DESCRIPTOR_TAG_COUNT; ++i)
        result.tags[i] = hash128_t{UINT64_C(0x1234567800000000) + i, 17};
    for (size_t i = 0; i < 256; ++i)
        result.byte_numbers[i] = hash128_t{UINT64_C(0x7654321000000000) + i, 19};
    return result;
}

physicality_descriptor_input_t body() {
    physicality_descriptor_input_t result{};
    result.entity_id = hash128_t{0xabc, 0xdef};
    result.type = 3;
    result.coord[0] = 0.25;
    result.coord[1] = -0.5;
    result.coord[2] = 0.125;
    result.coord[3] = 0.75;
    result.alignment_residual_is_null = 1;
    result.source_dim_is_null = 1;
    return result;
}

Plan build(const std::vector<physicality_descriptor_input_t>& inputs) {
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{256u * 1024u * 1024u};
    physicality_descriptor_plan_t* plan = nullptr;
    const auto result = physicality_descriptor_plan_build(
        inputs.data(), inputs.size(), &vocabulary, &limits, &plan);
    EXPECT_EQ(result, PHYSICALITY_DESCRIPTOR_OK);
    return Plan(plan, physicality_descriptor_plan_free);
}

std::vector<hash128_t> roots(const Plan& plan) {
    size_t count = 0;
    const auto* values = physicality_descriptor_plan_roots(plan.get(), &count);
    return count == 0 ? std::vector<hash128_t>{} :
        std::vector<hash128_t>(values, values + count);
}

const physicality_descriptor_node_t* node(const Plan& plan, const hash128_t& id) {
    size_t count = 0;
    const auto* nodes = physicality_descriptor_plan_nodes(plan.get(), &count);
    for (size_t i = 0; i < count; ++i)
        if (hash128_equals(&nodes[i].id, &id)) return &nodes[i];
    return nullptr;
}

void expect_different(const hash128_t& a, const hash128_t& b) {
    EXPECT_FALSE(hash128_equals(&a, &b));
}

TEST(PhysicalityDescriptor, ReusesExactFormAndSeparatesAlternateBodiesWithoutChangingEntity) {
    auto a = body();
    auto b = a;
    b.coord[0] = std::nextafter(a.coord[0], 1.0);
    auto c = a;
    c.type = 8;
    auto plan = build({a, b, a, c});
    ASSERT_NE(plan, nullptr);
    const auto ids = roots(plan);
    ASSERT_EQ(ids.size(), 4u);
    EXPECT_TRUE(hash128_equals(&ids[0], &ids[2]));
    expect_different(ids[0], ids[1]);
    expect_different(ids[0], ids[3]);
    const auto* children = physicality_descriptor_plan_children(plan.get(), nullptr);
    for (const auto& id : ids) {
        const auto* root = node(plan, id);
        ASSERT_NE(root, nullptr);
        ASSERT_EQ(root->child_count, 9u);
        EXPECT_TRUE(hash128_equals(&children[root->first_child + 1], &a.entity_id));
    }
}

TEST(PhysicalityDescriptor, RootIdentityIsIndependentOfBatchOrderAndUnrelatedNeighbors) {
    auto a = body();
    auto b = a;
    b.coord[1] = 0.875;
    const auto alone = build({a});
    const auto mixed = build({b, a});
    const auto reverse = build({a, b});
    ASSERT_NE(alone, nullptr);
    ASSERT_NE(mixed, nullptr);
    ASSERT_NE(reverse, nullptr);
    EXPECT_TRUE(hash128_equals(&roots(alone)[0], &roots(mixed)[1]));
    EXPECT_TRUE(hash128_equals(&roots(alone)[0], &roots(reverse)[0]));
}

TEST(PhysicalityDescriptor, EveryNodeUsesTheOrdinaryOrderedIdentityOwner) {
    const auto plan = build({body()});
    ASSERT_NE(plan, nullptr);
    size_t count = 0;
    const auto* nodes = physicality_descriptor_plan_nodes(plan.get(), &count);
    const auto* children = physicality_descriptor_plan_children(plan.get(), nullptr);
    ASSERT_GT(count, 5u);
    for (size_t i = 0; i < count; ++i) {
        hash128_t ordinary{};
        hash128_merkle(0, children + nodes[i].first_child, nodes[i].child_count, &ordinary);
        EXPECT_TRUE(hash128_equals(&ordinary, &nodes[i].id));
        for (size_t j = i + 1; j < count; ++j)
            expect_different(nodes[i].id, nodes[j].id);
    }
}

TEST(PhysicalityDescriptor, CarrierReferencesRemainOrdinaryChildrenAndKeepOccurrenceScope) {
    const hash128_t child{0x3456, 0x789a};
    const std::array<hash128_t, 2> repeated{child, child};
    double trajectory[8]{};
    ASSERT_EQ(trajectory_build(repeated.data(), repeated.size(), trajectory), 0);
    auto input = body();
    input.trajectory_xyzm = trajectory;
    input.trajectory_vertices = 2;
    input.n_constituents = 2;
    const auto plan = build({input, input});
    ASSERT_NE(plan, nullptr);
    size_t count = 0;
    const auto* refs = physicality_descriptor_plan_references(plan.get(), &count);
    ASSERT_EQ(count, 6u);
    for (size_t input_index = 0; input_index < 2; ++input_index) {
        const size_t offset = 3 * input_index;
        EXPECT_EQ(refs[offset].input_index, input_index);
        EXPECT_EQ(refs[offset].vertex_index, SIZE_MAX);
        EXPECT_EQ(refs[offset].kind, PHYSICALITY_DESCRIPTOR_REALIZED_ENTITY);
        for (size_t i = 1; i < 3; ++i) {
            EXPECT_TRUE(hash128_equals(&refs[offset + i].entity_id, &child));
            EXPECT_EQ(refs[offset + i].input_index, input_index);
            EXPECT_EQ(refs[offset + i].vertex_index, i - 1);
            EXPECT_EQ(refs[offset + i].kind, PHYSICALITY_DESCRIPTOR_CARRIER_ENTITY);
        }
    }
    const auto* nodes = physicality_descriptor_plan_nodes(plan.get(), &count);
    const auto* children = physicality_descriptor_plan_children(plan.get(), nullptr);
    const auto vocabulary = basis();
    size_t carriers = 0;
    for (size_t i = 0; i < count; ++i) {
        const auto* fields = children + nodes[i].first_child;
        if (hash128_equals(&fields[0], &vocabulary.tags[PHYSICALITY_DESCRIPTOR_CARRIER])) {
            ASSERT_EQ(nodes[i].child_count, 5u);
            EXPECT_TRUE(hash128_equals(&fields[1], &child));
            ++carriers;
        }
    }
    EXPECT_EQ(carriers, 2u);
}

TEST(PhysicalityDescriptor, PreservesRawCarrierWordsAndLogicalRunSemantics) {
    mantissa_payload_t payload{};
    payload.entity_id = hash128_t{0x77, 0x88};
    payload.ordinal = 0;
    payload.run_length = 1;
    double carrier[4][4]{};
    mantissa_pack(carrier[0], &payload);
    payload.ordinal = 1;
    mantissa_pack(carrier[1], &payload);
    payload.run_length = 0; // Legacy one-element representation remains exact.
    mantissa_pack(carrier[2], &payload);
    payload.flags = laplace_vertex_flags(200, 0, 0);
    mantissa_pack(carrier[3], &payload);
    std::vector<physicality_descriptor_input_t> inputs;
    for (const auto& vertex : carrier) {
        auto input = body();
        input.type = 1;
        input.entity_id = payload.entity_id;
        input.trajectory_xyzm = vertex;
        input.trajectory_vertices = 1;
        input.n_constituents = 1;
        inputs.push_back(input);
    }
    const auto plan = build(inputs);
    ASSERT_NE(plan, nullptr);
    const auto ids = roots(plan);
    for (size_t i = 0; i < ids.size(); ++i)
        for (size_t j = i + 1; j < ids.size(); ++j) expect_different(ids[i], ids[j]);
}

TEST(PhysicalityDescriptor, EncodesWideLogicalTrajectoryWithoutOrdinalCeiling) {
    const hash128_t child{0x11, 0x22};
    std::vector<hash128_t> expanded(65537u, child);
    std::vector<double> trajectory(expanded.size() * 4u);
    size_t vertices = 0;
    ASSERT_EQ(trajectory_build_rle(expanded.data(), expanded.size(), trajectory.data(), &vertices), 0);
    ASSERT_EQ(vertices, 2u);
    auto input = body();
    input.type = 1;
    hash128_merkle(0, expanded.data(), expanded.size(), &input.entity_id);
    input.trajectory_xyzm = trajectory.data();
    input.trajectory_vertices = vertices;
    input.n_constituents = static_cast<int32_t>(expanded.size());
    const auto plan = build({input});
    ASSERT_NE(plan, nullptr);
    size_t references = 0;
    physicality_descriptor_plan_references(plan.get(), &references);
    EXPECT_EQ(references, 3u);
}

TEST(PhysicalityDescriptor, FactorIdSizedPayloadDoesNotBecomeEntityReference) {
    const float factors[]{0.25f, -0.75f, 0.5f, 0.125f, 0.875f, -0.625f};
    double carrier[4]{};
    size_t vertices = 0;
    ASSERT_EQ(laplace_factor_pack_values(factors, 6, carrier, &vertices), 0);
    ASSERT_EQ(vertices, 1u);
    auto input = body();
    input.trajectory_xyzm = carrier;
    input.trajectory_vertices = 1;
    input.n_constituents = 1;
    const auto plan = build({input});
    ASSERT_NE(plan, nullptr);
    size_t count = 0;
    const auto* refs = physicality_descriptor_plan_references(plan.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_TRUE(hash128_equals(&refs[0].entity_id, &input.entity_id));
    const auto* nodes = physicality_descriptor_plan_nodes(plan.get(), &count);
    const auto* children = physicality_descriptor_plan_children(plan.get(), nullptr);
    const auto vocabulary = basis();
    size_t factors_found = 0;
    for (size_t i = 0; i < count; ++i)
        if (hash128_equals(&children[nodes[i].first_child],
                &vocabulary.tags[PHYSICALITY_DESCRIPTOR_FACTOR])) ++factors_found;
    EXPECT_EQ(factors_found, 1u);
}

TEST(PhysicalityDescriptor, ExactBinary64AndNullableFieldsHaveDistinctContent) {
    auto zero = body();
    zero.coord[0] = 0.0;
    auto negative_zero = zero;
    negative_zero.coord[0] = -0.0;
    auto present_zero = zero;
    present_zero.alignment_residual_is_null = 0;
    present_zero.alignment_residual = 0.0;
    auto dimension = zero;
    dimension.source_dim_is_null = 0;
    dimension.source_dim = 4;
    auto hilbert = zero;
    hilbert.hilbert_index.bytes[15] = 1;
    const auto plan = build({zero, negative_zero, present_zero, dimension, hilbert});
    ASSERT_NE(plan, nullptr);
    const auto ids = roots(plan);
    for (size_t i = 0; i < ids.size(); ++i)
        for (size_t j = i + 1; j < ids.size(); ++j) expect_different(ids[i], ids[j]);
}

TEST(PhysicalityDescriptor, RejectsInvalidBodyBeforePublishingAnyPlan) {
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{1024u * 1024u};
    const auto good = body();
    std::vector<physicality_descriptor_input_t> bad(8, good);
    bad[0].type = 0;
    bad[1].n_constituents = -1;
    bad[2].coord[2] = std::numeric_limits<double>::quiet_NaN();
    bad[3].alignment_residual_is_null = 2;
    bad[4].alignment_residual_is_null = 0;
    bad[4].alignment_residual = std::numeric_limits<double>::infinity();
    bad[5].source_dim_is_null = 0;
    bad[5].source_dim = 0;
    bad[6].trajectory_vertices = 1;
    bad[7].n_constituents = 1;
    for (const auto& value : bad) {
        const std::array<physicality_descriptor_input_t, 2> batch{good, value};
        physicality_descriptor_plan_t* output = nullptr;
        EXPECT_EQ(physicality_descriptor_plan_build(batch.data(), batch.size(), &vocabulary,
            &limits, &output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
        EXPECT_EQ(output, nullptr);
    }
}

TEST(PhysicalityDescriptor, RejectsContentParentMismatchAndNoncanonicalCarrierBits) {
    const hash128_t child{0x22, 0x33};
    double carrier[4]{};
    ASSERT_EQ(trajectory_build(&child, 1, carrier), 0);
    auto input = body();
    input.type = 1;
    input.trajectory_xyzm = carrier;
    input.trajectory_vertices = 1;
    input.n_constituents = 1;
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{1024u * 1024u};
    physicality_descriptor_plan_t* output = nullptr;
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary, &limits, &output),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
    input.type = 3;
    input.n_constituents = 2;
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary, &limits, &output),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    input.n_constituents = 1;
    carrier[0] = std::numeric_limits<double>::infinity();
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary, &limits, &output),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
}

TEST(PhysicalityDescriptor, EnforcesPeakAllocationIncludingEmptyBatch) {
    const auto vocabulary = basis();
    for (const auto& inputs : {std::vector<physicality_descriptor_input_t>{},
            std::vector<physicality_descriptor_input_t>{body()}}) {
        const auto plan = build(inputs);
        ASSERT_NE(plan, nullptr);
        const size_t retained = physicality_descriptor_plan_bytes(plan.get());
        const size_t peak = physicality_descriptor_plan_peak_bytes(plan.get());
        ASSERT_GT(retained, 0u);
        ASSERT_GE(peak, retained);
        physicality_descriptor_limits_t limits{0u};
        physicality_descriptor_plan_t* output = nullptr;
        EXPECT_EQ(physicality_descriptor_plan_build(inputs.data(), inputs.size(), &vocabulary,
            &limits, &output), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
        EXPECT_EQ(output, nullptr);
        limits.maximum_plan_bytes = peak;
        ASSERT_EQ(physicality_descriptor_plan_build(inputs.data(), inputs.size(), &vocabulary,
            &limits, &output), PHYSICALITY_DESCRIPTOR_OK);
        Plan bounded(output, physicality_descriptor_plan_free);
        EXPECT_LE(physicality_descriptor_plan_peak_bytes(bounded.get()), peak);
        const auto expected = roots(plan), actual = roots(bounded);
        ASSERT_EQ(actual.size(), expected.size());
        for (size_t i = 0; i < actual.size(); ++i)
            EXPECT_TRUE(hash128_equals(&actual[i], &expected[i]));
        if (inputs.empty()) {
            limits.maximum_plan_bytes = retained - 1u;
            output = nullptr;
            EXPECT_EQ(physicality_descriptor_plan_build(nullptr, 0u, &vocabulary,
                &limits, &output), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
            EXPECT_EQ(output, nullptr);
        }
    }
}

TEST(PhysicalityDescriptor, RepeatedFormsFitTheirActualGraphWithoutLosingOccurrences) {
    constexpr size_t occurrences = 4096u;
    constexpr size_t grant = 1024u * 1024u;
    const auto input = body();
    const auto singleton = build({input});
    ASSERT_NE(singleton, nullptr);
    std::vector<physicality_descriptor_input_t> inputs(occurrences, input);
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{grant};
    physicality_descriptor_plan_t* output = nullptr;
    // The previous estimate reserved 16 descriptor nodes per input before
    // seeing exact reuse. Even its node array alone exceeded this same grant.
    ASSERT_GT(occurrences * 16u * sizeof(physicality_descriptor_node_t), grant);
    ASSERT_EQ(physicality_descriptor_plan_build(inputs.data(), inputs.size(), &vocabulary,
        &limits, &output), PHYSICALITY_DESCRIPTOR_OK);
    Plan repeated(output, physicality_descriptor_plan_free);
    EXPECT_LE(physicality_descriptor_plan_peak_bytes(repeated.get()), grant);
    const auto expected = roots(singleton)[0];
    const auto actual = roots(repeated);
    ASSERT_EQ(actual.size(), occurrences);
    for (const auto& id : actual) EXPECT_TRUE(hash128_equals(&id, &expected));
    size_t count = 0u, single_count = 0u;
    const auto* nodes = physicality_descriptor_plan_nodes(repeated.get(), &count);
    const auto* single_nodes = physicality_descriptor_plan_nodes(singleton.get(), &single_count);
    ASSERT_EQ(count, single_count);
    for (size_t i = 0u; i < count; ++i) {
        EXPECT_TRUE(hash128_equals(&nodes[i].id, &single_nodes[i].id));
        EXPECT_EQ(nodes[i].first_child, single_nodes[i].first_child);
        EXPECT_EQ(nodes[i].child_count, single_nodes[i].child_count);
    }
    const auto* children = physicality_descriptor_plan_children(repeated.get(), &count);
    const auto* single_children = physicality_descriptor_plan_children(singleton.get(), &single_count);
    ASSERT_EQ(count, single_count);
    EXPECT_EQ(std::memcmp(children, single_children, count * sizeof(hash128_t)), 0);
    const auto* references = physicality_descriptor_plan_references(repeated.get(), &count);
    ASSERT_EQ(count, occurrences);
    for (size_t i = 0u; i < count; ++i) {
        EXPECT_TRUE(hash128_equals(&references[i].entity_id, &input.entity_id));
        EXPECT_EQ(references[i].input_index, i);
        EXPECT_EQ(references[i].vertex_index, SIZE_MAX);
        EXPECT_EQ(references[i].kind, PHYSICALITY_DESCRIPTOR_REALIZED_ENTITY);
    }
}

TEST(PhysicalityDescriptor, RehashAndArrayGrowthPreserveOrderedIdentityUnderFiniteGrants) {
    std::vector<physicality_descriptor_input_t> inputs;
    for (size_t i = 0u; i < 257u; ++i) {
        auto input = body();
        input.entity_id.lo += i;
        input.coord[0] = static_cast<double>(i) / 1024.0;
        input.hilbert_index.bytes[0] = static_cast<uint8_t>(i);
        inputs.push_back(input);
    }
    const auto reference = build(inputs);
    ASSERT_NE(reference, nullptr);
    const auto expected = roots(reference);
    ASSERT_EQ(expected.size(), inputs.size());
    for (size_t i = 0u; i < inputs.size(); ++i) {
        const auto scalar = build({inputs[i]});
        ASSERT_NE(scalar, nullptr);
        EXPECT_TRUE(hash128_equals(&expected[i], &roots(scalar)[0]));
    }
    const size_t retained = physicality_descriptor_plan_bytes(reference.get());
    const size_t peak = physicality_descriptor_plan_peak_bytes(reference.get());
    ASSERT_GT(peak, retained); // Replacement reserves the old and requested new arrays.
    const auto empty = build({});
    ASSERT_NE(empty, nullptr);
    const size_t fixed = physicality_descriptor_plan_bytes(empty.get())
        + inputs.size() * (sizeof(hash128_t) + sizeof(physicality_descriptor_reference_t));
    ASSERT_LT(fixed, retained);
    const auto vocabulary = basis();
    bool rejected_after_fixed = false, accepted = false;
    // Tight grants may shed geometric slack. Acceptance always proves the
    // conservative old+new reservation, not just retained storage, stayed within the grant.
    for (size_t grant : {fixed, fixed + 128u, retained - 1u, retained, peak - 1u, peak}) {
        const physicality_descriptor_limits_t limits{grant};
        physicality_descriptor_plan_t* output = nullptr;
        const auto status = physicality_descriptor_plan_build(inputs.data(), inputs.size(),
            &vocabulary, &limits, &output);
        if (status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED) {
            EXPECT_EQ(output, nullptr);
            rejected_after_fixed = true;
            continue;
        }
        ASSERT_EQ(status, PHYSICALITY_DESCRIPTOR_OK);
        ASSERT_NE(output, nullptr);
        Plan bounded(output, physicality_descriptor_plan_free);
        accepted = true;
        EXPECT_LE(physicality_descriptor_plan_bytes(bounded.get()),
            physicality_descriptor_plan_peak_bytes(bounded.get()));
        EXPECT_LE(physicality_descriptor_plan_peak_bytes(bounded.get()), grant);
        const auto actual = roots(bounded);
        ASSERT_EQ(actual.size(), expected.size());
        for (size_t i = 0u; i < actual.size(); ++i)
            EXPECT_TRUE(hash128_equals(&actual[i], &expected[i]));
        size_t expected_count = 0, actual_count = 0;
        const auto* expected_nodes = physicality_descriptor_plan_nodes(reference.get(), &expected_count);
        const auto* actual_nodes = physicality_descriptor_plan_nodes(bounded.get(), &actual_count);
        ASSERT_EQ(actual_count, expected_count);
        if (actual_count != 0)
            EXPECT_EQ(std::memcmp(actual_nodes, expected_nodes, actual_count * sizeof(*actual_nodes)), 0);
        const auto* expected_children = physicality_descriptor_plan_children(reference.get(), &expected_count);
        const auto* actual_children = physicality_descriptor_plan_children(bounded.get(), &actual_count);
        ASSERT_EQ(actual_count, expected_count);
        if (actual_count != 0)
            EXPECT_EQ(std::memcmp(actual_children, expected_children, actual_count * sizeof(*actual_children)), 0);
        const auto* expected_references = physicality_descriptor_plan_references(reference.get(), &expected_count);
        const auto* actual_references = physicality_descriptor_plan_references(bounded.get(), &actual_count);
        ASSERT_EQ(actual_count, expected_count);
        if (actual_count != 0)
            EXPECT_EQ(std::memcmp(actual_references, expected_references,
                                  actual_count * sizeof(*actual_references)), 0);
    }
    EXPECT_TRUE(rejected_after_fixed);
    EXPECT_TRUE(accepted);
}

TEST(PhysicalityDescriptor, RejectsArithmeticOverflowWithoutReadingBorrowedBody) {
    auto input = body();
    input.trajectory_vertices = SIZE_MAX;
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{SIZE_MAX};
    physicality_descriptor_plan_t* output = nullptr;
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary, &limits, &output),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(output, nullptr);
}

TEST(PhysicalityDescriptor, RejectsAliasedProviderVocabularyBeforeBinaryFieldsLoseInformation) {
    auto vocabulary = basis();
    ASSERT_EQ(physicality_descriptor_basis_is_valid(&vocabulary), 1);
    const auto input = body();
    const physicality_descriptor_limits_t limits{1024u * 1024u};
    physicality_descriptor_plan_t* output = nullptr;
    vocabulary.byte_numbers[1] = vocabulary.byte_numbers[0];
    EXPECT_EQ(physicality_descriptor_basis_is_valid(&vocabulary), 0);
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary, &limits, &output),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(output, nullptr);
    vocabulary = basis();
    vocabulary.tags[PHYSICALITY_DESCRIPTOR_SCHEMA] = vocabulary.byte_numbers[0];
    EXPECT_EQ(physicality_descriptor_basis_is_valid(&vocabulary), 0);
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary, &limits, &output),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(output, nullptr);
    vocabulary = basis();
    vocabulary.tags[PHYSICALITY_DESCRIPTOR_I16] = vocabulary.tags[PHYSICALITY_DESCRIPTOR_U16];
    EXPECT_EQ(physicality_descriptor_basis_is_valid(&vocabulary), 0);
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary, &limits, &output),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(output, nullptr);
}

void stage_body(intent_stage_t* stage, const physicality_descriptor_input_t& input, int64_t time) {
    hash128_t placement{};
    laplace_physicality_id_compute(input.entity_id, input.type, &placement);
    ASSERT_EQ(intent_stage_add_physicality(stage, &placement, &input.entity_id, input.type,
        input.coord, &input.hilbert_index, input.trajectory_xyzm,
        static_cast<uint32_t>(input.trajectory_vertices), input.n_constituents,
        input.alignment_residual_is_null, input.alignment_residual,
        input.source_dim_is_null, input.source_dim, time), 0);
}

Capture capture(const std::vector<const intent_stage_t*>& stages) {
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{8u * 1024u * 1024u};
    physicality_descriptor_capture_t* result = nullptr;
    EXPECT_EQ(physicality_descriptor_capture_stages(stages.data(), stages.size(), &vocabulary,
        &limits, 8u * 1024u * 1024u, &result), PHYSICALITY_DESCRIPTOR_OK);
    return Capture(result, physicality_descriptor_capture_free);
}

TEST(PhysicalityDescriptorStage, RepeatedRawFormsFitSameGrantAndKeepEveryObservation) {
    constexpr size_t occurrences = 4096u;
    constexpr size_t grant = 1024u * 1024u;
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    const auto input = body();
    for (size_t i = 0u; i < occurrences; ++i)
        stage_body(stage.get(), input, INTENT_STAGE_PG_EPOCH_UNIX_US + static_cast<int64_t>(i));
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{grant};
    const intent_stage_t* stages[]{stage.get()};
    physicality_descriptor_capture_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages(stages, 1u, &vocabulary,
        &limits, grant, &raw), PHYSICALITY_DESCRIPTOR_OK);
    Capture result(raw, physicality_descriptor_capture_free);
    EXPECT_LE(physicality_descriptor_capture_bytes(result.get()),
        physicality_descriptor_capture_peak_bytes(result.get()));
    EXPECT_LE(physicality_descriptor_capture_peak_bytes(result.get()), grant);
    const auto scalar = build({input});
    ASSERT_NE(scalar, nullptr);
    const auto expected = roots(scalar)[0];
    size_t count = 0u;
    const auto* ids = physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(result.get()), &count);
    ASSERT_EQ(count, occurrences);
    const auto* observations = physicality_descriptor_capture_observations(result.get(), &count);
    ASSERT_EQ(count, occurrences);
    for (size_t i = 0u; i < count; ++i) {
        EXPECT_TRUE(hash128_equals(&ids[i], &expected));
        EXPECT_EQ(observations[i].source_stage_index, 0u);
        EXPECT_EQ(observations[i].source_row_index, i);
        EXPECT_EQ(observations[i].observed_at_unix_us,
            INTENT_STAGE_PG_EPOCH_UNIX_US + static_cast<int64_t>(i));
    }
}

TEST(PhysicalityDescriptorStage, CapturesActualBodiesBeforePlacementDedupAndKeepsObservations) {
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    const auto a = body();
    auto b = a;
    b.coord[0] = 0.875;
    stage_body(stage.get(), a, 1700000000000000);
    stage_body(stage.get(), b, 1700000000000001);
    stage_body(stage.get(), a, 1700000000000002);
    const auto result = capture({stage.get()});
    ASSERT_NE(result, nullptr);
    size_t count = 0;
    const auto* observations = physicality_descriptor_capture_observations(result.get(), &count);
    ASSERT_EQ(count, 3u);
    const auto* ids = physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(result.get()), &count);
    ASSERT_EQ(count, 3u);
    EXPECT_TRUE(hash128_equals(&ids[0], &ids[2]));
    expect_different(ids[0], ids[1]);
    for (size_t i = 0; i < count; ++i) {
        EXPECT_TRUE(hash128_equals(&observations[0].placement_id, &observations[i].placement_id));
        EXPECT_EQ(observations[i].source_stage_index, 0u);
        EXPECT_EQ(observations[i].source_row_index, i);
        EXPECT_EQ(observations[i].observed_at_unix_us, 1700000000000000 + static_cast<int64_t>(i));
    }
    EXPECT_EQ(intent_stage_physicality_count(stage.get()), 3u);
}

TEST(PhysicalityDescriptorStage, ExactCopyReadbackRetainsBodyAndRetiresBorrowedSourceSafely) {
    const std::array<hash128_t, 2> members{hash128_t{12,34}, hash128_t{56,78}};
    double trajectory[8]{};
    ASSERT_EQ(trajectory_build(members.data(), members.size(), trajectory), 0);
    auto input = body();
    input.trajectory_xyzm = trajectory;
    input.trajectory_vertices = 2;
    input.n_constituents = 2;
    input.coord[0] = -0.0;
    input.alignment_residual_is_null = 0;
    input.alignment_residual = -0.0;
    input.source_dim_is_null = 0;
    input.source_dim = 17;
    for (size_t i = 0; i < sizeof(input.hilbert_index); ++i)
        input.hilbert_index.bytes[i] = static_cast<uint8_t>(i);
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    stage_body(stage.get(), input, -1234567);
    const auto result = capture({stage.get()});
    ASSERT_NE(result, nullptr);
    stage.reset();
    size_t count = 0;
    const auto* got = physicality_descriptor_capture_inputs(result.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_TRUE(hash128_equals(&got->entity_id, &input.entity_id));
    EXPECT_EQ(got->type, input.type);
    EXPECT_EQ(std::memcmp(got->coord, input.coord, sizeof(input.coord)), 0);
    EXPECT_EQ(std::memcmp(&got->hilbert_index, &input.hilbert_index, sizeof(input.hilbert_index)), 0);
    ASSERT_EQ(got->trajectory_vertices, input.trajectory_vertices);
    EXPECT_EQ(std::memcmp(got->trajectory_xyzm, trajectory, sizeof(trajectory)), 0);
    EXPECT_EQ(got->n_constituents, input.n_constituents);
    EXPECT_EQ(got->alignment_residual_is_null, 0);
    EXPECT_TRUE(std::signbit(got->alignment_residual));
    EXPECT_EQ(got->source_dim_is_null, 0);
    EXPECT_EQ(got->source_dim, 17);
    EXPECT_EQ(physicality_descriptor_capture_observations(result.get(), nullptr)->observed_at_unix_us,
        -1234567);
    const auto direct = build({input});
    EXPECT_TRUE(hash128_equals(&roots(direct)[0], physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(result.get()), nullptr)));
}

TEST(PhysicalityDescriptorStage, ReleasesOnlyValidatedPlanAndRetainsExactCapturedRows) {
    const std::array<hash128_t, 2> members{hash128_t{12,34}, hash128_t{56,78}};
    double trajectory[8]{};
    ASSERT_EQ(trajectory_build(members.data(), members.size(), trajectory), 0);
    auto a = body();
    a.trajectory_xyzm = trajectory;
    a.trajectory_vertices = 2;
    a.n_constituents = 2;
    a.coord[0] = -0.0;
    a.alignment_residual_is_null = 0;
    a.alignment_residual = -0.0;
    a.source_dim_is_null = 0;
    a.source_dim = 17;
    auto b = a;
    b.coord[1] = 0.875;
    const std::array<physicality_descriptor_input_t, 3> expected{a, b, a};
    const std::array<int64_t, 3> times{-1234567, 0, 1700000000123456};
    Stage first(intent_stage_new(0), intent_stage_free);
    Stage second(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(first, nullptr);
    ASSERT_NE(second, nullptr);
    stage_body(first.get(), a, times[0]);
    stage_body(first.get(), b, times[1]);
    stage_body(second.get(), a, times[2]);
    auto result = capture({first.get(), second.get()});
    ASSERT_NE(result, nullptr);
    const auto* plan = physicality_descriptor_capture_plan(result.get());
    ASSERT_NE(plan, nullptr);
    const size_t released_bytes = physicality_descriptor_plan_bytes(plan);
    const size_t retained_before = physicality_descriptor_capture_bytes(result.get());
    const size_t peak_before = physicality_descriptor_capture_peak_bytes(result.get());
    size_t count = 0;
    const auto* root_data = physicality_descriptor_plan_roots(plan, &count);
    ASSERT_EQ(count, expected.size());
    const std::vector<hash128_t> saved_roots(root_data, root_data + count);
    const auto* reference_data = physicality_descriptor_plan_references(plan, &count);
    ASSERT_EQ(count, 9u);
    const std::vector<physicality_descriptor_reference_t> saved_references(reference_data, reference_data + count);
    const auto* inputs = physicality_descriptor_capture_inputs(result.get(), &count);
    ASSERT_EQ(count, expected.size());
    const auto* observations = physicality_descriptor_capture_observations(result.get(), &count);
    ASSERT_EQ(count, expected.size());
    const std::array<const double*, 3> decoded{inputs[0].trajectory_xyzm,
        inputs[1].trajectory_xyzm, inputs[2].trajectory_xyzm};
    first.reset();
    second.reset();

    ASSERT_GT(released_bytes, 0u);
    EXPECT_EQ(physicality_descriptor_capture_release_plan(nullptr), 0u);
    EXPECT_EQ(physicality_descriptor_capture_release_plan(result.get()), released_bytes);
    EXPECT_EQ(physicality_descriptor_capture_plan(result.get()), nullptr);
    EXPECT_EQ(physicality_descriptor_capture_bytes(result.get()), retained_before - released_bytes);
    EXPECT_EQ(physicality_descriptor_capture_peak_bytes(result.get()), peak_before);
    EXPECT_EQ(physicality_descriptor_capture_release_plan(result.get()), 0u);
    EXPECT_EQ(physicality_descriptor_capture_bytes(result.get()), retained_before - released_bytes);
    EXPECT_EQ(physicality_descriptor_capture_peak_bytes(result.get()), peak_before);
    EXPECT_EQ(physicality_descriptor_capture_inputs(result.get(), &count), inputs);
    ASSERT_EQ(count, expected.size());
    EXPECT_EQ(physicality_descriptor_capture_observations(result.get(), &count), observations);
    ASSERT_EQ(count, expected.size());
    for (size_t i = 0; i < expected.size(); ++i) {
        EXPECT_TRUE(hash128_equals(&inputs[i].entity_id, &expected[i].entity_id));
        EXPECT_EQ(inputs[i].type, expected[i].type);
        EXPECT_EQ(std::memcmp(inputs[i].coord, expected[i].coord, sizeof(a.coord)), 0);
        EXPECT_EQ(std::memcmp(&inputs[i].hilbert_index, &expected[i].hilbert_index, sizeof(a.hilbert_index)), 0);
        EXPECT_EQ(inputs[i].trajectory_xyzm, decoded[i]);
        ASSERT_EQ(inputs[i].trajectory_vertices, 2u);
        EXPECT_EQ(std::memcmp(inputs[i].trajectory_xyzm, trajectory, sizeof(trajectory)), 0);
        EXPECT_EQ(inputs[i].n_constituents, 2);
        EXPECT_EQ(inputs[i].alignment_residual_is_null, 0);
        EXPECT_TRUE(std::signbit(inputs[i].alignment_residual));
        EXPECT_EQ(inputs[i].source_dim_is_null, 0);
        EXPECT_EQ(inputs[i].source_dim, 17);
        hash128_t placement{};
        laplace_physicality_id_compute(expected[i].entity_id, expected[i].type, &placement);
        EXPECT_TRUE(hash128_equals(&observations[i].placement_id, &placement));
        EXPECT_EQ(observations[i].source_stage_index, i == 2 ? 1u : 0u);
        EXPECT_EQ(observations[i].source_row_index, i == 2 ? 0u : i);
        EXPECT_EQ(observations[i].observed_at_unix_us, times[i]);
    }
    const auto rebuilt = build(std::vector<physicality_descriptor_input_t>(inputs, inputs + count));
    ASSERT_NE(rebuilt, nullptr);
    const auto rebuilt_roots = roots(rebuilt);
    ASSERT_EQ(rebuilt_roots.size(), saved_roots.size());
    EXPECT_EQ(std::memcmp(rebuilt_roots.data(), saved_roots.data(), saved_roots.size() * sizeof(hash128_t)), 0);
    const auto* rebuilt_references = physicality_descriptor_plan_references(rebuilt.get(), &count);
    ASSERT_EQ(count, saved_references.size());
    for (size_t i = 0; i < count; ++i) {
        EXPECT_TRUE(hash128_equals(&rebuilt_references[i].entity_id, &saved_references[i].entity_id));
        EXPECT_EQ(rebuilt_references[i].input_index, saved_references[i].input_index);
        EXPECT_EQ(rebuilt_references[i].vertex_index, saved_references[i].vertex_index);
        EXPECT_EQ(rebuilt_references[i].kind, saved_references[i].kind);
    }
}

TEST(PhysicalityDescriptorStage, PreservesIdentityAcrossActualStagePartitionBoundaries) {
    Stage together(intent_stage_new(0), intent_stage_free);
    Stage first(intent_stage_new(0), intent_stage_free);
    Stage second(intent_stage_new(0), intent_stage_free);
    auto a = body();
    auto b = a;
    b.coord[1] = 0.875;
    stage_body(together.get(), a, 0);
    stage_body(together.get(), b, 1);
    stage_body(first.get(), a, 0);
    stage_body(second.get(), b, 1);
    const auto one = capture({together.get()});
    const auto two = capture({first.get(), second.get()});
    ASSERT_NE(one, nullptr);
    ASSERT_NE(two, nullptr);
    const auto* x = physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(one.get()), nullptr);
    const auto* y = physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(two.get()), nullptr);
    EXPECT_EQ(std::memcmp(x, y, 2u * sizeof(hash128_t)), 0);
    const auto* observations = physicality_descriptor_capture_observations(two.get(), nullptr);
    EXPECT_EQ(observations[0].source_stage_index, 0u);
    EXPECT_EQ(observations[1].source_stage_index, 1u);
    EXPECT_EQ(observations[1].source_row_index, 0u);
}

TEST(PhysicalityDescriptorStage, RejectsMalformedActualTupleAndIncorrectPlacementKey) {
    Stage stage(intent_stage_new(0), intent_stage_free);
    stage_body(stage.get(), body(), 0);
    size_t bytes = 0;
    const auto* data = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &bytes);
    ASSERT_GT(bytes, 22u);
    auto* mutable_data = const_cast<uint8_t*>(data);
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{8u * 1024u * 1024u};
    physicality_descriptor_capture_t* output = nullptr;
    mutable_data[6] ^= 1; // Mutate the retained placement id, not a test double.
    EXPECT_EQ(physicality_descriptor_capture_stages(
        std::array<const intent_stage_t*,1>{stage.get()}.data(), 1, &vocabulary,
        &limits, 8u * 1024u * 1024u, &output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
    mutable_data[6] ^= 1;
    mutable_data[1] = 9; // Physicality COPY row must have exactly ten fields.
    EXPECT_EQ(physicality_descriptor_capture_stages(
        std::array<const intent_stage_t*,1>{stage.get()}.data(), 1, &vocabulary,
        &limits, 8u * 1024u * 1024u, &output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
}

TEST(PhysicalityDescriptorStage, EnforcesSeparateCaptureAndPlanPeakBudgets) {
    Stage stage(intent_stage_new(0), intent_stage_free);
    stage_body(stage.get(), body(), 0);
    const auto result = capture({stage.get()});
    ASSERT_NE(result, nullptr);
    const size_t retained = physicality_descriptor_capture_bytes(result.get());
    const size_t peak = physicality_descriptor_capture_peak_bytes(result.get());
    ASSERT_GE(peak, retained);
    const auto vocabulary = basis();
    physicality_descriptor_limits_t limits{8u * 1024u * 1024u};
    physicality_descriptor_capture_t* output = nullptr;
    const std::array<const intent_stage_t*,1> stages{stage.get()};
    EXPECT_EQ(physicality_descriptor_capture_stages(stages.data(), stages.size(), &vocabulary,
        &limits, 0u, &output), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(output, nullptr);
    limits.maximum_plan_bytes = 0;
    EXPECT_EQ(physicality_descriptor_capture_stages(stages.data(), stages.size(), &vocabulary,
        &limits, peak, &output), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(output, nullptr);
    limits.maximum_plan_bytes = peak;
    ASSERT_EQ(physicality_descriptor_capture_stages(stages.data(), stages.size(), &vocabulary,
        &limits, peak, &output), PHYSICALITY_DESCRIPTOR_OK);
    Capture bounded(output, physicality_descriptor_capture_free);
    EXPECT_LE(physicality_descriptor_capture_peak_bytes(bounded.get()), peak);
    EXPECT_TRUE(hash128_equals(physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(result.get()), nullptr),
        physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(bounded.get()), nullptr)));
}

struct Catalog {
    std::vector<physicality_descriptor_node_t> nodes;
    std::vector<hash128_t> children;
    std::vector<hash128_t> roots;
    explicit Catalog(const Plan& plan) {
        size_t count = 0;
        const auto* n = physicality_descriptor_plan_nodes(plan.get(), &count);
        nodes.assign(n, n + count);
        const auto* c = physicality_descriptor_plan_children(plan.get(), &count);
        children.assign(c, c + count);
        const auto* r = physicality_descriptor_plan_roots(plan.get(), &count);
        roots.assign(r, r + count);
    }

    physicality_descriptor_status_t read(physicality_descriptor_readback_t** out,
        size_t maximum_bytes = 8u * 1024u * 1024u) const {
        const auto vocabulary = basis();
        const physicality_descriptor_limits_t limits{8u * 1024u * 1024u};
        return physicality_descriptor_readback_build(nodes.data(), nodes.size(),
            children.data(), children.size(), roots.data(), roots.size(), &vocabulary,
            &limits, maximum_bytes, out);
    }

    void rehash() {
        std::vector<std::pair<hash128_t, hash128_t>> replacements;
        for (auto& n : nodes) {
            for (size_t i = 0; i < n.child_count; ++i)
                for (const auto& change : replacements)
                    if (hash128_equals(&children[n.first_child + i], &change.first)) {
                        children[n.first_child + i] = change.second;
                        break;
                    }
            const auto old = n.id;
            hash128_merkle(0, children.data() + n.first_child, n.child_count, &n.id);
            replacements.emplace_back(old, n.id);
        }
        for (auto& root : roots)
            for (const auto& change : replacements)
                if (hash128_equals(&root, &change.first)) {
                    root = change.second;
                    break;
                }
    }
};

TEST(PhysicalityDescriptorReadback, ReconstructsExactBodyFromOrdinaryCompositionRecords) {
    mantissa_payload_t carrier{};
    carrier.entity_id = hash128_t{0xabcd, 0x1234};
    carrier.ordinal = 0;
    carrier.run_length = 7;
    carrier.flags = laplace_vertex_flags(200, 0, 0);
    double trajectory[4]{};
    mantissa_pack(trajectory, &carrier);
    auto a = body();
    a.coord[0] = -0.0;
    a.alignment_residual_is_null = 0;
    a.alignment_residual = -0.0;
    a.source_dim_is_null = 0;
    a.source_dim = 4096;
    a.trajectory_xyzm = trajectory;
    a.trajectory_vertices = 1;
    a.n_constituents = 7;
    a.hilbert_index.bytes[3] = 0xfe;
    auto b = a;
    b.type = 8;
    b.coord[2] = 0.875;
    const auto plan = build({a, b, a});
    ASSERT_NE(plan, nullptr);
    const Catalog catalog(plan);
    physicality_descriptor_readback_t* raw = nullptr;
    ASSERT_EQ(catalog.read(&raw), PHYSICALITY_DESCRIPTOR_OK);
    Readback readback(raw, physicality_descriptor_readback_free);
    size_t count = 0;
    const auto* inputs = physicality_descriptor_readback_inputs(readback.get(), &count);
    ASSERT_EQ(count, 3u);
    const std::array<physicality_descriptor_input_t, 3> expected{a, b, a};
    for (size_t i = 0; i < count; ++i) {
        const auto& x = inputs[i];
        const auto& y = expected[i];
        EXPECT_TRUE(hash128_equals(&x.entity_id, &y.entity_id));
        EXPECT_EQ(x.type, y.type);
        EXPECT_EQ(std::memcmp(x.coord, y.coord, sizeof(x.coord)), 0);
        EXPECT_EQ(std::memcmp(&x.hilbert_index, &y.hilbert_index, sizeof(x.hilbert_index)), 0);
        ASSERT_EQ(x.trajectory_vertices, 1u);
        EXPECT_EQ(std::memcmp(x.trajectory_xyzm, trajectory, sizeof(trajectory)), 0);
        EXPECT_EQ(x.n_constituents, 7);
        EXPECT_EQ(x.alignment_residual_is_null, 0);
        EXPECT_TRUE(std::signbit(x.alignment_residual));
        EXPECT_EQ(x.source_dim_is_null, 0);
        EXPECT_EQ(x.source_dim, 4096);
    }
}

TEST(PhysicalityDescriptorReadback, RejectsGinCandidateWithWrongTypedFieldDespiteValidContentIdentity) {
    const auto plan = build({body()});
    ASSERT_NE(plan, nullptr);
    Catalog catalog(plan);
    const auto vocabulary = basis();
    size_t altered = 0;
    for (const auto& n : catalog.nodes)
        if (hash128_equals(&catalog.children[n.first_child],
                &vocabulary.tags[PHYSICALITY_DESCRIPTOR_BINARY64])) {
            catalog.children[n.first_child] = vocabulary.tags[PHYSICALITY_DESCRIPTOR_U64];
            ++altered;
            break;
        }
    ASSERT_EQ(altered, 1u);
    catalog.rehash(); // All identities are valid, and root still directly contains schema + E.
    physicality_descriptor_readback_t* output = nullptr;
    EXPECT_EQ(catalog.read(&output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
}

TEST(PhysicalityDescriptorReadback, RejectsWrongSchemaAndMissingOrForgedCompositionRecords) {
    const auto plan = build({body()});
    ASSERT_NE(plan, nullptr);
    physicality_descriptor_readback_t* output = nullptr;
    Catalog wrong_schema(plan);
    wrong_schema.children[wrong_schema.nodes.back().first_child] = basis().tags[PHYSICALITY_DESCRIPTOR_CARRIER];
    wrong_schema.rehash();
    EXPECT_EQ(wrong_schema.read(&output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
    Catalog missing(plan);
    missing.nodes.erase(missing.nodes.begin());
    EXPECT_EQ(missing.read(&output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
    Catalog forged(plan);
    forged.nodes[0].id.lo ^= 1;
    EXPECT_EQ(forged.read(&output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
}

TEST(PhysicalityDescriptorReadback, ReconstructsFactorWordsWithoutIntroducingCarrierReferences) {
    const float factors[]{0.25f, -0.75f, 0.5f, 0.125f, 0.875f, -0.625f};
    double trajectory[4]{};
    size_t vertices = 0;
    ASSERT_EQ(laplace_factor_pack_values(factors, 6, trajectory, &vertices), 0);
    auto input = body();
    input.trajectory_xyzm = trajectory;
    input.trajectory_vertices = vertices;
    input.n_constituents = 1;
    const auto plan = build({input});
    ASSERT_NE(plan, nullptr);
    const Catalog catalog(plan);
    physicality_descriptor_readback_t* raw = nullptr;
    ASSERT_EQ(catalog.read(&raw), PHYSICALITY_DESCRIPTOR_OK);
    Readback readback(raw, physicality_descriptor_readback_free);
    const auto* decoded = physicality_descriptor_readback_inputs(readback.get(), nullptr);
    ASSERT_EQ(decoded->trajectory_vertices, 1u);
    EXPECT_EQ(std::memcmp(decoded->trajectory_xyzm, trajectory, sizeof(trajectory)), 0);
}

TEST(PhysicalityDescriptorReadback, AccountsForItsPeakWorksetAndRejectsInvalidOffsets) {
    const auto plan = build({body()});
    ASSERT_NE(plan, nullptr);
    Catalog catalog(plan);
    physicality_descriptor_readback_t* raw = nullptr;
    ASSERT_EQ(catalog.read(&raw), PHYSICALITY_DESCRIPTOR_OK);
    Readback readback(raw, physicality_descriptor_readback_free);
    const size_t retained = physicality_descriptor_readback_bytes(readback.get());
    const size_t bytes = physicality_descriptor_readback_peak_bytes(readback.get());
    EXPECT_GT(bytes, retained);
    const size_t plan_peak = physicality_descriptor_plan_peak_bytes(plan.get());
    size_t slots = 1u;
    while (slots < catalog.nodes.size() * 2u) slots *= 2u;
    EXPECT_EQ(bytes, retained + slots * sizeof(size_t) + plan_peak);
    physicality_descriptor_readback_t* output = nullptr;
    // A grant consumed by the retained decoder/catalog has no room for the
    // required verification plan, regardless of adaptive graph capacity.
    EXPECT_EQ(catalog.read(&output, retained + slots * sizeof(size_t)),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(output, nullptr);
    ASSERT_EQ(catalog.read(&output, bytes), PHYSICALITY_DESCRIPTOR_OK);
    Readback bounded(output, physicality_descriptor_readback_free);
    EXPECT_LE(physicality_descriptor_readback_peak_bytes(bounded.get()), bytes);
    output = nullptr;
    catalog.nodes[0].first_child = SIZE_MAX;
    EXPECT_EQ(catalog.read(&output), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(output, nullptr);
}




TEST(PhysicalityDescriptor, DiagnosedPlannerPreservesOutputsAndExactFiniteGrantDecisions) {
    std::vector<physicality_descriptor_input_t> inputs;
    for (size_t i = 0; i < 16u; ++i) {
        auto input = body();
        input.entity_id.lo += i;
        input.coord[0] += static_cast<double>(i) / 64.0;
        inputs.push_back(input);
    }
    const auto vocabulary = basis();
    const auto reference = build(inputs);
    ASSERT_NE(reference, nullptr);
    const size_t peak = physicality_descriptor_plan_peak_bytes(reference.get());
    bool saw_initial = false, saw_growth = false, saw_success = false;
    for (size_t grant = 0; grant <= peak + 128u; grant += 128u) {
        const physicality_descriptor_limits_t limits{grant};
        physicality_descriptor_plan_t *plain_raw = nullptr, *diagnosed_raw = nullptr;
        physicality_descriptor_plan_diagnostics_t diagnostic;
        std::memset(&diagnostic, 0xa5, sizeof(diagnostic));
        const auto plain_status = physicality_descriptor_plan_build(
            inputs.data(), inputs.size(), &vocabulary, &limits, &plain_raw);
        const auto diagnosed_status = physicality_descriptor_plan_build_diagnosed_cancelable(
            inputs.data(), inputs.size(), &vocabulary, &limits, nullptr, &diagnostic, &diagnosed_raw);
        Plan plain(plain_raw, physicality_descriptor_plan_free);
        Plan diagnosed(diagnosed_raw, physicality_descriptor_plan_free);
        ASSERT_EQ(diagnosed_status, plain_status) << "grant=" << grant;
        EXPECT_EQ(diagnostic.maximum_bytes, grant);
        EXPECT_EQ(diagnostic.input_count, inputs.size());
        EXPECT_LE(diagnostic.completed_inputs, inputs.size());
        EXPECT_LE(diagnostic.retained_bytes, diagnostic.peak_bytes);
        EXPECT_LE(diagnostic.peak_bytes, grant);
        EXPECT_LE(diagnostic.node_count, diagnostic.node_capacity);
        EXPECT_LE(diagnostic.child_count, diagnostic.child_capacity);
        EXPECT_LE(diagnostic.reference_count, diagnostic.reference_capacity);
        if (plain_status != PHYSICALITY_DESCRIPTOR_OK) {
            ASSERT_EQ(plain_status, PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
            EXPECT_EQ(plain.get(), nullptr);
            EXPECT_EQ(diagnosed.get(), nullptr);
            ASSERT_EQ(diagnostic.refusal, PHYSICALITY_DESCRIPTOR_PLAN_GRANT_REFUSED);
            EXPECT_GT(diagnostic.requested_bytes, grant - diagnostic.retained_bytes);
            EXPECT_LT(diagnostic.completed_inputs, inputs.size());
            if (diagnostic.allocation == PHYSICALITY_DESCRIPTOR_PLAN_INITIAL) {
                saw_initial = true;
                EXPECT_EQ(diagnostic.retained_bytes, 0u);
                EXPECT_EQ(diagnostic.completed_inputs, 0u);
            } else {
                saw_growth = true;
                EXPECT_TRUE(diagnostic.allocation == PHYSICALITY_DESCRIPTOR_PLAN_NODES ||
                    diagnostic.allocation == PHYSICALITY_DESCRIPTOR_PLAN_CHILDREN ||
                    diagnostic.allocation == PHYSICALITY_DESCRIPTOR_PLAN_SLOTS);
                EXPECT_GT(diagnostic.retained_bytes, 0u);
            }
            continue;
        }
        saw_success = true;
        ASSERT_NE(plain, nullptr);
        ASSERT_NE(diagnosed, nullptr);
        EXPECT_EQ(diagnostic.refusal, PHYSICALITY_DESCRIPTOR_PLAN_NO_REFUSAL);
        EXPECT_EQ(diagnostic.allocation, PHYSICALITY_DESCRIPTOR_PLAN_NO_ALLOCATION);
        EXPECT_EQ(diagnostic.requested_bytes, 0u);
        EXPECT_EQ(diagnostic.completed_inputs, inputs.size());
        EXPECT_EQ(diagnostic.retained_bytes, physicality_descriptor_plan_bytes(plain.get()));
        EXPECT_EQ(diagnostic.peak_bytes, physicality_descriptor_plan_peak_bytes(plain.get()));
        const auto plain_roots = roots(plain), diagnosed_roots = roots(diagnosed);
        ASSERT_EQ(plain_roots.size(), diagnosed_roots.size());
        EXPECT_EQ(std::memcmp(plain_roots.data(), diagnosed_roots.data(),
            plain_roots.size() * sizeof(hash128_t)), 0);
        size_t n = 0, m = 0;
        const auto* pn = physicality_descriptor_plan_nodes(plain.get(), &n);
        const auto* dn = physicality_descriptor_plan_nodes(diagnosed.get(), &m);
        ASSERT_EQ(n, m);
        EXPECT_EQ(std::memcmp(pn, dn, n * sizeof(*pn)), 0);
        const auto* pc = physicality_descriptor_plan_children(plain.get(), &n);
        const auto* dc = physicality_descriptor_plan_children(diagnosed.get(), &m);
        ASSERT_EQ(n, m);
        EXPECT_EQ(std::memcmp(pc, dc, n * sizeof(*pc)), 0);
        const auto* pr = physicality_descriptor_plan_references(plain.get(), &n);
        const auto* dr = physicality_descriptor_plan_references(diagnosed.get(), &m);
        ASSERT_EQ(n, m);
        EXPECT_EQ(std::memcmp(pr, dr, n * sizeof(*pr)), 0);
    }
    EXPECT_TRUE(saw_initial);
    EXPECT_TRUE(saw_growth);
    EXPECT_TRUE(saw_success);
}

TEST(PhysicalityDescriptor, DiagnosedPlannerKeepsInvalidBodyCancellationAndOverflowDistinct) {
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{1024u * 1024u};
    std::array<physicality_descriptor_input_t, 2> inputs{body(), body()};
    inputs[1].coord[0] = std::numeric_limits<double>::quiet_NaN();
    physicality_descriptor_plan_diagnostics_t diagnostic{};
    physicality_descriptor_plan_t* raw = nullptr;
    EXPECT_EQ(physicality_descriptor_plan_build_diagnosed_cancelable(
        inputs.data(), inputs.size(), &vocabulary, &limits, nullptr, &diagnostic, &raw),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(raw, nullptr);
    EXPECT_EQ(diagnostic.refusal, PHYSICALITY_DESCRIPTOR_PLAN_NO_REFUSAL);
    EXPECT_EQ(diagnostic.completed_inputs, 1u);
    EXPECT_GT(diagnostic.node_count, 0u);
    const physicality_descriptor_cancel_t cancelled{
        [](void*) -> int { return 1; }, nullptr};
    EXPECT_EQ(physicality_descriptor_plan_build_diagnosed_cancelable(
        inputs.data(), inputs.size(), &vocabulary, &limits, &cancelled, &diagnostic, &raw),
        PHYSICALITY_DESCRIPTOR_CANCELLED);
    EXPECT_EQ(raw, nullptr);
    EXPECT_EQ(diagnostic.refusal, PHYSICALITY_DESCRIPTOR_PLAN_NO_REFUSAL);
    EXPECT_EQ(diagnostic.completed_inputs, 0u);
    EXPECT_EQ(diagnostic.retained_bytes, 0u);
    EXPECT_EQ(physicality_descriptor_plan_build_diagnosed_cancelable(
        inputs.data(), SIZE_MAX, &vocabulary, &limits, nullptr, &diagnostic, &raw),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(raw, nullptr);
    EXPECT_EQ(diagnostic.allocation, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL);
    EXPECT_EQ(diagnostic.refusal, PHYSICALITY_DESCRIPTOR_PLAN_SIZE_OVERFLOW);
    EXPECT_EQ(diagnostic.requested_bytes, 0u);
    EXPECT_EQ(diagnostic.retained_bytes, 0u);
}


TEST(PhysicalityDescriptor, SourcePlanBoundCoversActualRecipeAndOccurrenceArrays) {
    const auto vocabulary = basis();
    const std::array<hash128_t, 3> operands{{{11, 21}, {12, 22}, {11, 21}}};
    std::array<double, 12> trajectory{};
    ASSERT_EQ(trajectory_build(operands.data(), operands.size(), trajectory.data()), 0);
    for (size_t count : {size_t{0}, size_t{1}, size_t{16}, size_t{257}}) {
        std::vector<physicality_descriptor_input_t> inputs;
        for (size_t i = 0u; i < count; ++i) {
            auto input = body();
            input.entity_id.lo += i;
            input.coord[0] += static_cast<double>(i) / 512.0;
            input.trajectory_xyzm = trajectory.data();
            input.trajectory_vertices = operands.size();
            input.n_constituents = static_cast<int32_t>(operands.size());
            input.alignment_residual_is_null = 0;
            input.alignment_residual = 0.125;
            input.source_dim_is_null = 0;
            input.source_dim = 4;
            inputs.push_back(input);
        }
        size_t bound = 0u;
        ASSERT_EQ(physicality_descriptor_plan_payload_bound(count, count * operands.size(),
            count == 0u ? 0u : operands.size(), &bound), PHYSICALITY_DESCRIPTOR_OK);
        const physicality_descriptor_limits_t limits{bound};
        physicality_descriptor_plan_t* raw = nullptr;
        ASSERT_EQ(physicality_descriptor_plan_build(inputs.data(), inputs.size(), &vocabulary,
            &limits, &raw), PHYSICALITY_DESCRIPTOR_OK);
        Plan plan(raw, physicality_descriptor_plan_free);
        EXPECT_LE(physicality_descriptor_plan_peak_bytes(plan.get()), bound);
        size_t roots_count = 0u, references_count = 0u;
        physicality_descriptor_plan_roots(plan.get(), &roots_count);
        physicality_descriptor_plan_references(plan.get(), &references_count);
        EXPECT_EQ(roots_count, count);
        EXPECT_EQ(references_count, count * (operands.size() + 1u));
    }
    // The larger factor recipe and both nullable fields are covered too.
    const float values[]{0.25f, -0.75f, 0.5f, 0.125f, 0.875f, -0.625f};
    double factor_vertex[4]{};
    size_t factor_vertices = 0u;
    ASSERT_EQ(laplace_factor_pack_values(values, 6u, factor_vertex, &factor_vertices), 0);
    auto factor = body();
    factor.trajectory_xyzm = factor_vertex; factor.trajectory_vertices = factor_vertices;
    factor.n_constituents = 1; factor.alignment_residual_is_null = 0;
    factor.alignment_residual = 0.25; factor.source_dim_is_null = 0; factor.source_dim = 4;
    size_t factor_bound = 0u;
    ASSERT_EQ(physicality_descriptor_plan_payload_bound(1u, factor_vertices, factor_vertices, &factor_bound),
        PHYSICALITY_DESCRIPTOR_OK);
    const physicality_descriptor_limits_t factor_limits{factor_bound};
    physicality_descriptor_plan_t* factor_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_plan_build(&factor, 1u, &vocabulary, &factor_limits, &factor_raw),
        PHYSICALITY_DESCRIPTOR_OK);
    Plan factor_plan(factor_raw, physicality_descriptor_plan_free);
    EXPECT_LE(physicality_descriptor_plan_peak_bytes(factor_plan.get()), factor_bound);
    size_t unchanged = 987u;
    EXPECT_EQ(physicality_descriptor_plan_payload_bound(SIZE_MAX, 0u, 0u, &unchanged),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(unchanged, 987u);
    EXPECT_EQ(physicality_descriptor_plan_payload_bound(1u, SIZE_MAX, SIZE_MAX, &unchanged),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(unchanged, 987u);
    EXPECT_EQ(physicality_descriptor_plan_payload_bound(0u, 1u, 1u, &unchanged),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(physicality_descriptor_plan_payload_bound(1u, 1u, 2u, &unchanged),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(physicality_descriptor_plan_payload_bound(1u, 100u, 1u, &unchanged),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(unchanged, 987u);
}

TEST(PhysicalityDescriptorStage, ShapeAndPayloadBoundsUseActualOrdinaryTupleFrames) {
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    const hash128_t source{501, 502}, type{601, 602}, relation{701, 702}, attestation{801, 802};
    auto empty = body();
    empty.alignment_residual_is_null = 0;
    empty.alignment_residual = 0.25;
    empty.source_dim_is_null = 0;
    empty.source_dim = 4;
    auto point = empty, line = empty;
    const std::array<hash128_t, 2> operands{{{21, 31}, {22, 32}}};
    std::array<double, 8> trajectory{};
    ASSERT_EQ(trajectory_build(operands.data(), operands.size(), trajectory.data()), 0);
    point.trajectory_xyzm = trajectory.data(); point.trajectory_vertices = 1u; point.n_constituents = 1;
    line.trajectory_xyzm = trajectory.data(); line.trajectory_vertices = 2u; line.n_constituents = 2;
    stage_body(stage.get(), empty, INTENT_STAGE_PG_EPOCH_UNIX_US + 1);
    stage_body(stage.get(), point, INTENT_STAGE_PG_EPOCH_UNIX_US + 2);
    stage_body(stage.get(), line, INTENT_STAGE_PG_EPOCH_UNIX_US + 3);
    ASSERT_EQ(intent_stage_add_entity(stage.get(), &empty.entity_id, 4, &type, &source), 0);
    std::array<uint8_t, 32> mask{}; mask.fill(0xff);
    ASSERT_EQ(intent_stage_add_attestation_mode(stage.get(), &attestation, &empty.entity_id,
        &relation, &source, &source, &type, 1, INTENT_STAGE_PG_EPOCH_UNIX_US + 4,
        1, 1, 0, 0, 1, mask.data()), 0);
    const intent_stage_t* stages[]{stage.get()};
    physicality_descriptor_shape_t shape{};
    ASSERT_EQ(physicality_descriptor_stages_shape(stages, 1u, &shape), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(shape.forms, 3u);
    EXPECT_EQ(shape.stored_vertices, 3u);
    EXPECT_EQ(shape.maximum_vertices, 2u);
    size_t capture_bytes = 0u, tuple_bound = 0u;
    ASSERT_EQ(physicality_descriptor_capture_payload_bound(shape.forms, shape.stored_vertices,
        &capture_bytes), PHYSICALITY_DESCRIPTOR_OK);
    physicality_descriptor_capture_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stage_rows(stages, 1u, capture_bytes, &raw),
        PHYSICALITY_DESCRIPTOR_OK);
    Capture owned(raw, physicality_descriptor_capture_free);
    EXPECT_EQ(physicality_descriptor_capture_bytes(owned.get()), capture_bytes);
    EXPECT_EQ(physicality_descriptor_capture_peak_bytes(owned.get()), capture_bytes);
    raw = nullptr;
    EXPECT_EQ(physicality_descriptor_capture_stage_rows(stages, 1u, capture_bytes - 1u, &raw),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(raw, nullptr);
    ASSERT_EQ(intent_stage_tuple_payload_bound(1u, 3u, 3u, 1u, &tuple_bound), 0);
    size_t e = 0u, p = 0u, a = 0u;
    intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ENTITIES, &e);
    const auto* physicalities = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &p);
    intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_ATTESTATIONS, &a);
    EXPECT_EQ(e, 68u);
    EXPECT_EQ(a, 229u);
    EXPECT_EQ(p, (153u + 158u + 162u) + 3u * 32u);
    EXPECT_EQ(tuple_bound, 68u + 3u * 162u + 3u * 32u + 229u);
    EXPECT_LE(e + p + a, tuple_bound);
    size_t unchanged = 987u;
    EXPECT_EQ(intent_stage_tuple_payload_bound(SIZE_MAX, 0u, 0u, 0u, &unchanged), -2);
    EXPECT_EQ(intent_stage_tuple_payload_bound(0u, 0u, 1u, 0u, &unchanged), -1);
    EXPECT_EQ(physicality_descriptor_capture_payload_bound(SIZE_MAX, 0u, &unchanged),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(unchanged, 987u);
    // The shared scanner must reject malformed tuple framing without publishing partial shape.
    auto* mutable_bytes = const_cast<uint8_t*>(physicalities);
    const uint8_t saved = mutable_bytes[1];
    mutable_bytes[1] = 9;
    shape = {901u, 902u, 903u};
    EXPECT_EQ(physicality_descriptor_stages_shape(stages, 1u, &shape), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(shape.forms, 901u);
    EXPECT_EQ(shape.stored_vertices, 902u);
    EXPECT_EQ(shape.maximum_vertices, 903u);
    mutable_bytes[1] = saved;
}


TEST(PhysicalityDescriptorStage, ConstantTimeProducerShapeBoundsExactRowsWithoutChangingStage) {
    Stage stage(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    physicality_descriptor_shape_t upper{901u, 902u, 903u}, exact{};
    ASSERT_EQ(physicality_descriptor_stage_shape_bound(stage.get(), &upper), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(upper.forms, 0u);
    EXPECT_EQ(upper.stored_vertices, 0u);
    EXPECT_EQ(upper.maximum_vertices, 0u);
    const std::array<hash128_t, 3> operands{{{51, 61}, {52, 62}, {51, 61}}};
    std::array<double, 12> trajectory{};
    ASSERT_EQ(trajectory_build(operands.data(), operands.size(), trajectory.data()), 0);
    const intent_stage_t* stages[]{stage.get()};
    for (size_t i = 0u; i < 32u; ++i) {
        auto input = body();
        input.trajectory_xyzm = i % 2u == 0u ? nullptr : trajectory.data();
        input.trajectory_vertices = i % 2u == 0u ? 0u : operands.size();
        input.n_constituents = static_cast<int32_t>(input.trajectory_vertices);
        stage_body(stage.get(), input, INTENT_STAGE_PG_EPOCH_UNIX_US + static_cast<int64_t>(i));
        size_t before_bytes = 0u, after_bytes = 0u;
        const auto* before = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &before_bytes);
        const std::vector<uint8_t> saved(before, before + before_bytes);
        ASSERT_EQ(physicality_descriptor_stage_shape_bound(stage.get(), &upper), PHYSICALITY_DESCRIPTOR_OK);
        ASSERT_EQ(physicality_descriptor_stages_shape(stages, 1u, &exact), PHYSICALITY_DESCRIPTOR_OK);
        EXPECT_EQ(upper.forms, exact.forms);
        EXPECT_GE(upper.stored_vertices, exact.stored_vertices);
        EXPECT_GE(upper.maximum_vertices, exact.maximum_vertices);
        EXPECT_EQ(upper.stored_vertices, before_bytes / 32u);
        const auto* after = intent_stage_tuple_ptr(stage.get(), INTENT_STAGE_TABLE_PHYSICALITIES, &after_bytes);
        ASSERT_EQ(after_bytes, before_bytes);
        EXPECT_EQ(std::memcmp(saved.data(), after, after_bytes), 0);
        size_t upper_plan = 0u, exact_plan = 0u;
        ASSERT_EQ(physicality_descriptor_plan_payload_bound(upper.forms, upper.stored_vertices,
            upper.maximum_vertices, &upper_plan), PHYSICALITY_DESCRIPTOR_OK);
        ASSERT_EQ(physicality_descriptor_plan_payload_bound(exact.forms, exact.stored_vertices,
            exact.maximum_vertices, &exact_plan), PHYSICALITY_DESCRIPTOR_OK);
        EXPECT_GE(upper_plan, exact_plan);
    }
    upper = {901u, 902u, 903u};
    EXPECT_EQ(physicality_descriptor_stage_shape_bound(nullptr, &upper), PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(upper.forms, 901u);
    EXPECT_EQ(upper.stored_vertices, 902u);
    EXPECT_EQ(upper.maximum_vertices, 903u);
}


struct PublicPlanRows {
    std::vector<physicality_descriptor_node_t> nodes;
    std::vector<hash128_t> children, roots;
    std::vector<physicality_descriptor_reference_t> references;
};

PublicPlanRows public_plan_rows(const Plan& plan) {
    PublicPlanRows result;
    size_t count = 0;
    const auto* nodes = physicality_descriptor_plan_nodes(plan.get(), &count);
    if (count) result.nodes.assign(nodes, nodes + count);
    const auto* children = physicality_descriptor_plan_children(plan.get(), &count);
    if (count) result.children.assign(children, children + count);
    const auto* roots = physicality_descriptor_plan_roots(plan.get(), &count);
    if (count) result.roots.assign(roots, roots + count);
    const auto* refs = physicality_descriptor_plan_references(plan.get(), &count);
    if (count) result.references.assign(refs, refs + count);
    return result;
}

/* Explicit public fields only: C/C++ structure padding is not evidence. These
 * complete bytes are compared across the actual baseline/candidate libraries
 * by the hosted owner, and against independently built scalar plans below. */
std::vector<uint8_t> public_plan_bytes(const PublicPlanRows& rows) {
    std::vector<uint8_t> bytes;
    const auto word = [&](uint64_t value) {
        for (size_t i = 0; i < 8; ++i)
            bytes.push_back(static_cast<uint8_t>(value >> (8u * i)));
    };
    const auto id = [&](const hash128_t& value) { word(value.lo); word(value.hi); };
    word(rows.nodes.size()); word(rows.children.size());
    word(rows.roots.size()); word(rows.references.size());
    for (const auto& node : rows.nodes) {
        id(node.id); word(node.first_child); word(node.child_count);
    }
    for (const auto& child : rows.children) id(child);
    for (const auto& root : rows.roots) id(root);
    for (const auto& reference : rows.references) {
        id(reference.entity_id); word(reference.input_index);
        word(reference.vertex_index); word(static_cast<uint64_t>(reference.kind));
    }
    return bytes;
}

void expect_scalar_plan_parity(const std::vector<physicality_descriptor_input_t>& inputs,
    const Plan& combined) {
    PublicPlanRows expected;
    std::set<std::array<uint64_t, 2>> seen;
    for (size_t input = 0; input < inputs.size(); ++input) {
        const auto scalar = build({inputs[input]});
        ASSERT_NE(scalar, nullptr);
        const auto rows = public_plan_rows(scalar);
        ASSERT_EQ(rows.roots.size(), 1u);
        expected.roots.push_back(rows.roots[0]);
        for (auto node : rows.nodes) {
            if (!seen.insert({node.id.lo, node.id.hi}).second) continue;
            const size_t first = node.first_child;
            node.first_child = expected.children.size();
            expected.nodes.push_back(node);
            expected.children.insert(expected.children.end(), rows.children.begin() + first,
                rows.children.begin() + first + node.child_count);
        }
        for (auto reference : rows.references) {
            reference.input_index = input;
            expected.references.push_back(reference);
        }
    }
    EXPECT_EQ(public_plan_bytes(public_plan_rows(combined)), public_plan_bytes(expected));
}

TEST(PhysicalityDescriptor, AdjacentBodyReuseMatchesScalarPlansAcrossEveryRecipeField) {
    const std::array<hash128_t, 2> carriers{{{0x123, 0x456}, {0x789, 0xabc}}};
    std::array<double, 12> trajectory{};
    ASSERT_EQ(trajectory_build(carriers.data(), carriers.size(), trajectory.data()), 0);
    const float factors[]{0.25f, -0.75f, 0.5f, 0.125f, 0.875f, -0.625f};
    size_t factors_written = 0;
    ASSERT_EQ(laplace_factor_pack_values(factors, 6, trajectory.data() + 8, &factors_written), 0);
    ASSERT_EQ(factors_written, 1u);
    auto copied_trajectory = trajectory; // Equal bytes, different borrowed address.
    auto changed_trajectory = trajectory;
    const std::array<hash128_t, 2> other{{{0x123, 0x457}, {0x789, 0xabc}}};
    ASSERT_EQ(trajectory_build(other.data(), other.size(), changed_trajectory.data()), 0);
    auto original = body();
    original.coord[0] = 0.0;
    original.trajectory_xyzm = trajectory.data();
    original.trajectory_vertices = 3;
    original.n_constituents = 3;
    original.alignment_residual_is_null = 0;
    original.alignment_residual = 0.0;
    original.source_dim_is_null = 0;
    original.source_dim = 4;
    auto copy = original;
    copy.trajectory_xyzm = copied_trajectory.data();
    std::vector<physicality_descriptor_input_t> variants;
    auto changed = original; changed.entity_id.lo ^= 1u; variants.push_back(changed);
    changed = original; changed.type = 8; variants.push_back(changed);
    changed = original; changed.coord[0] = -0.0; variants.push_back(changed);
    changed = original; changed.coord[3] = std::nextafter(changed.coord[3], 1.0); variants.push_back(changed);
    changed = original; changed.hilbert_index.bytes[15] ^= 1u; variants.push_back(changed);
    changed = original; changed.trajectory_xyzm = changed_trajectory.data(); variants.push_back(changed);
    changed = original; changed.alignment_residual = -0.0; variants.push_back(changed);
    changed = original; changed.alignment_residual_is_null = 1; variants.push_back(changed);
    changed = original; changed.source_dim = 5; variants.push_back(changed);
    changed = original; changed.source_dim_is_null = 1; variants.push_back(changed);
    changed = original; changed.trajectory_vertices = 2; changed.n_constituents = 2; variants.push_back(changed);
    changed = original; changed.trajectory_xyzm = nullptr; changed.trajectory_vertices = 0;
    changed.n_constituents = 0; variants.push_back(changed);
    std::vector<physicality_descriptor_input_t> inputs{original, copy};
    for (const auto& variant : variants) {
        inputs.push_back(variant); inputs.push_back(variant); inputs.push_back(copy);
    }
    // Inactive payloads are not part of the descriptor recipe.
    changed = original; changed.alignment_residual_is_null = 1; changed.source_dim_is_null = 1;
    inputs.push_back(changed);
    changed.alignment_residual = std::numeric_limits<double>::quiet_NaN(); changed.source_dim = -99;
    inputs.push_back(changed);
    const auto combined = build(inputs);
    ASSERT_NE(combined, nullptr);
    expect_scalar_plan_parity(inputs, combined);
    const auto rows = public_plan_rows(combined);
    ASSERT_GE(rows.references.size(), 6u);
    for (size_t input = 0; input < 2; ++input) {
        // One realized entity + two ordinary carriers; factor has no reference.
        for (size_t i = 0; i < 3; ++i) EXPECT_EQ(rows.references[input * 3 + i].input_index, input);
    }
}

TEST(PhysicalityDescriptor, AdjacentBodyReuseCannotHideInvalidFollowingBody) {
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{1024u * 1024u};
    const std::array<hash128_t, 2> carriers{{{11, 12}, {13, 14}}};
    std::array<double, 8> trajectory{};
    ASSERT_EQ(trajectory_build(carriers.data(), carriers.size(), trajectory.data()), 0);
    auto valid = body();
    valid.trajectory_xyzm = trajectory.data(); valid.trajectory_vertices = 2; valid.n_constituents = 2;
    std::vector<physicality_descriptor_input_t> invalid;
    auto changed = valid; changed.coord[0] = std::numeric_limits<double>::quiet_NaN(); invalid.push_back(changed);
    changed = valid; changed.trajectory_xyzm = nullptr; invalid.push_back(changed);
    changed = valid; changed.n_constituents = 1; invalid.push_back(changed);
    changed = valid; changed.alignment_residual_is_null = 2; invalid.push_back(changed);
    changed = valid; changed.source_dim_is_null = 0; changed.source_dim = 0; invalid.push_back(changed);
    changed = valid; changed.type = 1; invalid.push_back(changed); // False Content parent.
    for (const auto& bad : invalid) {
        const std::array<physicality_descriptor_input_t, 3> inputs{{valid, valid, bad}};
        physicality_descriptor_plan_t* raw = nullptr;
        physicality_descriptor_plan_diagnostics_t diagnostics{};
        EXPECT_EQ(physicality_descriptor_plan_build_diagnosed_cancelable(inputs.data(), inputs.size(),
            &vocabulary, &limits, nullptr, &diagnostics, &raw), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
        EXPECT_EQ(raw, nullptr);
        EXPECT_EQ(diagnostics.completed_inputs, 2u);
        const auto retry = build({valid, valid});
        ASSERT_NE(retry, nullptr);
        expect_scalar_plan_parity({valid, valid}, retry);
    }
}

struct BodyReuseCancellation {
    size_t calls = 0, stop = SIZE_MAX;
    static int requested(void* context) {
        auto& self = *static_cast<BodyReuseCancellation*>(context);
        return ++self.calls >= self.stop;
    }
};

TEST(PhysicalityDescriptor, AdjacentBodyReusePreservesCancellationAndFiniteGrantDecisions) {
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{16u * 1024u * 1024u};
    std::array<hash128_t, 64> carriers{};
    for (size_t i = 0; i < carriers.size(); ++i) carriers[i] = hash128_t{100u + i, 200u + i};
    std::array<double, 256> trajectory{};
    ASSERT_EQ(trajectory_build(carriers.data(), carriers.size(), trajectory.data()), 0);
    auto input = body();
    input.trajectory_xyzm = trajectory.data(); input.trajectory_vertices = carriers.size();
    input.n_constituents = static_cast<int32_t>(carriers.size());
    const std::vector<physicality_descriptor_input_t> inputs{input, input};
    BodyReuseCancellation observed;
    const physicality_descriptor_cancel_t callback{BodyReuseCancellation::requested, &observed};
    physicality_descriptor_plan_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_plan_build_cancelable(inputs.data(), inputs.size(),
        &vocabulary, &limits, &callback, &raw), PHYSICALITY_DESCRIPTOR_OK);
    Plan full(raw, physicality_descriptor_plan_free);
    const auto expected = public_plan_bytes(public_plan_rows(full));
    const size_t peak = physicality_descriptor_plan_peak_bytes(full.get());
    ASSERT_GT(observed.calls, 2u * (carriers.size() + 1u));
    // In the candidate these stops cover duplicate trajectory comparison and
    // reference-copy work; baseline also must unwind without a partial plan.
    for (size_t stop : {observed.calls - 2u * carriers.size(),
                        observed.calls - carriers.size(), observed.calls}) {
        BodyReuseCancellation interrupted{0u, stop};
        const physicality_descriptor_cancel_t cancel{BodyReuseCancellation::requested, &interrupted};
        raw = nullptr;
        EXPECT_EQ(physicality_descriptor_plan_build_cancelable(inputs.data(), inputs.size(),
            &vocabulary, &limits, &cancel, &raw), PHYSICALITY_DESCRIPTOR_CANCELLED);
        EXPECT_EQ(raw, nullptr);
        EXPECT_EQ(interrupted.calls, stop);
    }
    bool refused = false, accepted = false;
    for (size_t grant : {peak / 8u, peak / 4u, peak / 2u, peak - 1u, peak}) {
        const physicality_descriptor_limits_t bounded{grant};
        physicality_descriptor_plan_diagnostics_t diagnostic{};
        raw = nullptr;
        const auto status = physicality_descriptor_plan_build_diagnosed_cancelable(
            inputs.data(), inputs.size(), &vocabulary, &bounded, nullptr, &diagnostic, &raw);
        Plan result(raw, physicality_descriptor_plan_free);
        if (status == PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED) {
            refused = true; EXPECT_EQ(result, nullptr);
            EXPECT_EQ(diagnostic.refusal, PHYSICALITY_DESCRIPTOR_PLAN_GRANT_REFUSED);
        } else {
            ASSERT_EQ(status, PHYSICALITY_DESCRIPTOR_OK);
            accepted = true; ASSERT_NE(result, nullptr);
            EXPECT_LE(physicality_descriptor_plan_peak_bytes(result.get()), grant);
            EXPECT_EQ(public_plan_bytes(public_plan_rows(result)), expected);
        }
    }
    EXPECT_TRUE(refused); EXPECT_TRUE(accepted);
    expect_scalar_plan_parity(inputs, full);
}

TEST(PhysicalityDescriptor, AdjacentBodyRecipeWorkload) {
    constexpr size_t forms = 2048, vertices = 8, samples = 5;
    const auto vocabulary = basis();
    const physicality_descriptor_limits_t limits{64u * 1024u * 1024u};
    std::array<hash128_t, vertices> carriers{};
    for (size_t i = 0; i < vertices; ++i) carriers[i] = hash128_t{301u + i, 401u + i};
    std::array<double, vertices * 4> trajectory{};
    ASSERT_EQ(trajectory_build(carriers.data(), vertices, trajectory.data()), 0);
    auto copied = trajectory;
    auto input = body();
    input.trajectory_xyzm = trajectory.data(); input.trajectory_vertices = vertices;
    input.n_constituents = vertices;
    const std::array<const char*, 3> labels{{"adjacent", "alternating", "unique"}};
    RecordProperty("forms", std::to_string(forms));
    RecordProperty("vertices_per_form", std::to_string(vertices));
    RecordProperty("finite_grant_bytes", std::to_string(limits.maximum_plan_bytes));
    for (size_t pattern = 0; pattern < labels.size(); ++pattern) {
        std::vector<physicality_descriptor_input_t> inputs(forms, input);
        for (size_t i = 0; i < forms; ++i) {
            inputs[i].trajectory_xyzm = i % 2u ? copied.data() : trajectory.data();
            const size_t variant = pattern == 0 ? i / 64u : pattern == 1 ? i % 2u : i;
            inputs[i].coord[0] = static_cast<double>(variant) / static_cast<double>(forms);
        }
        std::vector<uint8_t> expected;
        size_t retained = 0, peak = 0;
        for (size_t sample = 0; sample <= samples; ++sample) {
            physicality_descriptor_plan_t* raw = nullptr;
            const auto started = std::chrono::steady_clock::now();
            const auto status = physicality_descriptor_plan_build(inputs.data(), inputs.size(),
                &vocabulary, &limits, &raw);
            const auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - started).count();
            ASSERT_EQ(status, PHYSICALITY_DESCRIPTOR_OK);
            Plan plan(raw, physicality_descriptor_plan_free);
            ASSERT_NE(plan, nullptr);
            const auto bytes = public_plan_bytes(public_plan_rows(plan));
            if (sample == 0) {
                expected = bytes;
                retained = physicality_descriptor_plan_bytes(plan.get());
                peak = physicality_descriptor_plan_peak_bytes(plan.get());
                if (const char* directory = std::getenv("LAPLACE_BODY_REUSE_EVIDENCE_DIR")) {
                    const std::filesystem::path root(directory);
                    ASSERT_TRUE(root.is_absolute());
                    std::filesystem::create_directories(root);
                    std::ofstream output(root / (std::string(labels[pattern]) + ".plan"), std::ios::binary);
                    ASSERT_TRUE(output.good());
                    output.write(reinterpret_cast<const char*>(bytes.data()),
                        static_cast<std::streamsize>(bytes.size()));
                    output.close();
                    ASSERT_TRUE(output.good());
                }
            }
            EXPECT_EQ(bytes, expected);
            EXPECT_EQ(physicality_descriptor_plan_bytes(plan.get()), retained);
            EXPECT_EQ(physicality_descriptor_plan_peak_bytes(plan.get()), peak);
            EXPECT_LE(peak, limits.maximum_plan_bytes);
            if (sample != 0)
                RecordProperty(std::string(labels[pattern]) + "_sample" + std::to_string(sample) + "_ns",
                    std::to_string(elapsed));
        }
        RecordProperty(std::string(labels[pattern]) + "_retained_bytes", std::to_string(retained));
        RecordProperty(std::string(labels[pattern]) + "_peak_bytes", std::to_string(peak));
    }
}

} // namespace
