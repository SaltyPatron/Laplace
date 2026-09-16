#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
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
    ASSERT_GT(peak, retained); // A real replaced array coexisted with its old buffer.
    const auto empty = build({});
    ASSERT_NE(empty, nullptr);
    const size_t fixed = physicality_descriptor_plan_bytes(empty.get())
        + inputs.size() * (sizeof(hash128_t) + sizeof(physicality_descriptor_reference_t));
    ASSERT_LT(fixed, retained);
    const auto vocabulary = basis();
    bool rejected_after_fixed = false, accepted = false;
    // Tight grants may shed geometric slack. Acceptance always proves the
    // measured old+new peak, not just retained storage, stayed within the grant.
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

} // namespace
