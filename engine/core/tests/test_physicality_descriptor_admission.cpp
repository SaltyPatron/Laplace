#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <chrono>
#include <string>
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/trajectory.h"
#include "../src/physicality_descriptor_provider.h"

namespace {

using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
using Vocabulary = std::unique_ptr<physicality_descriptor_vocabulary_t,
    decltype(&physicality_descriptor_vocabulary_free)>;
using Capture = std::unique_ptr<physicality_descriptor_capture_t,
    decltype(&physicality_descriptor_capture_free)>;
using Materialization = std::unique_ptr<physicality_descriptor_materialization_t,
    decltype(&physicality_descriptor_materialization_free)>;
constexpr size_t kBudget = 64u * 1024u * 1024u;
const hash128_t kSource{123, 456};
const hash128_t kUnit{789, 1011};

struct Body {
    physicality_descriptor_input_t value{};
    std::vector<double> trajectory;
    physicality_descriptor_input_t input() const {
        auto result = value;
        result.trajectory_xyzm = trajectory.empty() ? nullptr : trajectory.data();
        return result;
    }
};

Body atom(uint32_t codepoint) {
    Body result;
    result.value.type = 1;
    result.value.alignment_residual_is_null = 1;
    result.value.source_dim_is_null = 1;
    EXPECT_EQ(codepoint_table_resolve_atom(codepoint, &result.value.entity_id,
        result.value.coord, &result.value.hilbert_index), 0);
    return result;
}

Body composition(const std::vector<Body>& children) {
    Body result;
    result.value.type = 1;
    result.value.alignment_residual_is_null = 1;
    result.value.source_dim_is_null = 1;
    std::vector<hash128_t> ids;
    std::vector<double> coordinates;
    for (const auto& child : children) {
        ids.push_back(child.value.entity_id);
        coordinates.insert(coordinates.end(), child.value.coord, child.value.coord + 4u);
    }
    hash_composer_compose_node(4, ids.data(), coordinates.data(), ids.size(),
        &result.value.entity_id, result.value.coord, &result.value.hilbert_index);
    result.trajectory.resize(children.size() * 4u);
    EXPECT_EQ(trajectory_build(ids.data(), ids.size(), result.trajectory.data()), 0);
    result.value.trajectory_vertices = children.size();
    result.value.n_constituents = static_cast<int32_t>(children.size());
    return result;
}

Stage stage(const std::vector<Body>& bodies, const std::vector<int64_t>& times = {}) {
    Stage result(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    if (!result) { ADD_FAILURE() << "native stage allocation failed"; return result; }
    std::vector<physicality_descriptor_input_t> inputs;
    std::vector<int64_t> observed;
    for (size_t i = 0; i < bodies.size(); ++i) {
        inputs.push_back(bodies[i].input());
        observed.push_back(times.empty() ? 100 + static_cast<int64_t>(i) : times[i]);
    }
    EXPECT_EQ(physicality_descriptor_stage_add_batch(result.get(), inputs.data(), nullptr, observed.data(), inputs.size()),
        PHYSICALITY_DESCRIPTOR_OK);
    return result;
}

class PhysicalityDescriptorAdmission : public ::testing::Test {
protected:
    Vocabulary vocabulary{nullptr, physicality_descriptor_vocabulary_free};

    void SetUp() override {
        ASSERT_TRUE(codepoint_table_is_loaded());
        physicality_descriptor_vocabulary_t* raw = nullptr;
        ASSERT_EQ(physicality_descriptor_vocabulary_create(&kSource, kBudget, &raw), PHYSICALITY_DESCRIPTOR_OK);
        vocabulary.reset(raw);
    }

    Capture capture(const intent_stage_t* input) {
        physicality_descriptor_capture_t* raw = nullptr;
        const physicality_descriptor_limits_t limits{kBudget};
        EXPECT_EQ(physicality_descriptor_capture_stages(&input, 1,
            physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits, kBudget, &raw),
            PHYSICALITY_DESCRIPTOR_OK);
        return Capture(raw, physicality_descriptor_capture_free);
    }

    physicality_descriptor_status_t run(const Capture& input,
        const std::vector<const intent_stage_t*>& current,
        const std::vector<const intent_stage_t*>& admitted,
        const std::vector<hash128_t>& missing,
        const std::vector<physicality_descriptor_source_observation_t>& sources,
        Materialization& output, size_t budget = kBudget) {
        physicality_descriptor_materialization_t* raw = nullptr;
        const auto status = physicality_descriptor_materialize(input.get(), vocabulary.get(),
            current.data(), current.size(), admitted.data(), admitted.size(),
            missing.data(), missing.size(), sources.data(), sources.size(),
            &kSource, 200, budget, &raw);
        output.reset(raw);
        return status;
    }

    physicality_descriptor_admitted_form_t form(const Materialization& materialized, size_t index = 0) {
        size_t count = 0;
        const auto* forms = physicality_descriptor_materialization_forms(materialized.get(), &count);
        EXPECT_GT(count, index);
        return count > index ? forms[index] : physicality_descriptor_admitted_form_t{};
    }

    static std::vector<physicality_descriptor_source_observation_t> witnesses(size_t count) {
        return std::vector<physicality_descriptor_source_observation_t>(count, {kSource, kUnit, 0.8});
    }
};


struct DescriptorCancellationProbe {
    size_t calls = 0;
    size_t stop = std::numeric_limits<size_t>::max();
    static int requested(void* opaque) {
        auto& probe = *static_cast<DescriptorCancellationProbe*>(opaque);
        return ++probe.calls >= probe.stop;
    }
};

TEST_F(PhysicalityDescriptorAdmission, PhasedCaptureRetiresEncodedSourceBeforeCompleteValidation) {
    const auto body = composition({atom('A'), atom('B')});
    std::vector<Body> bodies(4096, body);
    auto original = stage(bodies);
    ASSERT_NE(original, nullptr);
    const intent_stage_t* source = original.get();
    const size_t encoded_bytes = intent_stage_memory_bytes(source);
    auto legacy = capture(source);
    ASSERT_NE(legacy, nullptr);

    physicality_descriptor_capture_t* decoded_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stage_rows_cancelable(
        &source, 1, kBudget, nullptr, &decoded_raw), PHYSICALITY_DESCRIPTOR_OK);
    Capture decoded(decoded_raw, physicality_descriptor_capture_free);
    const size_t decoded_bytes = physicality_descriptor_capture_bytes(decoded.get());
    const size_t required_occurrence_bytes = bodies.size() *
        (sizeof(hash128_t) + 3u * sizeof(physicality_descriptor_reference_t));
    const size_t aggregate = encoded_bytes + decoded_bytes + required_occurrence_bytes - 1u;
    ASSERT_LE(encoded_bytes + physicality_descriptor_capture_peak_bytes(decoded.get()), aggregate);
    // Even mandatory roots/reference arrays cannot coexist with the encoded
    // source under this grant. This refusal does not depend on growth slack.
    physicality_descriptor_capture_t* refused = nullptr;
    physicality_descriptor_limits_t limits{aggregate - encoded_bytes};
    EXPECT_EQ(physicality_descriptor_capture_stages(&source, 1,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits,
        aggregate - encoded_bytes, &refused), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(refused, nullptr);

    original.reset(); // only the imported encoded copy is retired
    size_t input_count = 0;
    const auto* inputs = physicality_descriptor_capture_inputs(decoded.get(), &input_count);
    ASSERT_EQ(input_count, bodies.size());
    limits.maximum_plan_bytes = aggregate - decoded_bytes;
    physicality_descriptor_plan_t* validation_raw = nullptr;
    ASSERT_EQ(physicality_descriptor_plan_build_cancelable(inputs, input_count,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits,
        nullptr, &validation_raw), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_plan_t, decltype(&physicality_descriptor_plan_free)>
        validation(validation_raw, physicality_descriptor_plan_free);
    ASSERT_LE(decoded_bytes + physicality_descriptor_plan_peak_bytes(validation.get()), aggregate);
    const auto* expected_plan = physicality_descriptor_capture_plan(legacy.get());
    size_t expected_count = 0, actual_count = 0;
    const auto* expected_roots = physicality_descriptor_plan_roots(expected_plan, &expected_count);
    const auto* actual_roots = physicality_descriptor_plan_roots(validation.get(), &actual_count);
    ASSERT_EQ(actual_count, expected_count);
    for (size_t i = 0; i < actual_count; ++i)
        EXPECT_TRUE(hash128_equals(&actual_roots[i], &expected_roots[i]));
    const auto* expected_nodes = physicality_descriptor_plan_nodes(expected_plan, &expected_count);
    const auto* actual_nodes = physicality_descriptor_plan_nodes(validation.get(), &actual_count);
    ASSERT_EQ(actual_count, expected_count);
    for (size_t i = 0; i < actual_count; ++i) {
        EXPECT_TRUE(hash128_equals(&actual_nodes[i].id, &expected_nodes[i].id));
        EXPECT_EQ(actual_nodes[i].first_child, expected_nodes[i].first_child);
        EXPECT_EQ(actual_nodes[i].child_count, expected_nodes[i].child_count);
    }
    const auto* expected_children = physicality_descriptor_plan_children(expected_plan, &expected_count);
    const auto* actual_children = physicality_descriptor_plan_children(validation.get(), &actual_count);
    ASSERT_EQ(actual_count, expected_count);
    for (size_t i = 0; i < actual_count; ++i)
        EXPECT_TRUE(hash128_equals(&actual_children[i], &expected_children[i]));
    const auto* expected_refs = physicality_descriptor_plan_references(expected_plan, &expected_count);
    const auto* actual_refs = physicality_descriptor_plan_references(validation.get(), &actual_count);
    ASSERT_EQ(actual_count, expected_count);
    for (size_t i = 0; i < actual_count; ++i) {
        EXPECT_TRUE(hash128_equals(&actual_refs[i].entity_id, &expected_refs[i].entity_id));
        EXPECT_EQ(actual_refs[i].input_index, expected_refs[i].input_index);
        EXPECT_EQ(actual_refs[i].vertex_index, expected_refs[i].vertex_index);
        EXPECT_EQ(actual_refs[i].kind, expected_refs[i].kind);
    }
    validation.reset();
    const auto* expected_observations = physicality_descriptor_capture_observations(legacy.get(), &expected_count);
    const auto* actual_observations = physicality_descriptor_capture_observations(decoded.get(), &actual_count);
    ASSERT_EQ(actual_count, expected_count);
    for (size_t i = 0; i < actual_count; ++i) {
        EXPECT_TRUE(hash128_equals(&actual_observations[i].placement_id, &expected_observations[i].placement_id));
        EXPECT_EQ(actual_observations[i].source_stage_index, expected_observations[i].source_stage_index);
        EXPECT_EQ(actual_observations[i].source_row_index, expected_observations[i].source_row_index);
        EXPECT_EQ(actual_observations[i].observed_at_unix_us, expected_observations[i].observed_at_unix_us);
    }
    Materialization before(nullptr, physicality_descriptor_materialization_free);
    Materialization after(nullptr, physicality_descriptor_materialization_free);
    const auto sources = witnesses(bodies.size());
    ASSERT_EQ(run(legacy, {}, {}, {}, sources, before), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(decoded, {}, {}, {}, sources, after), PHYSICALITY_DESCRIPTOR_OK);
    const auto* before_forms = physicality_descriptor_materialization_forms(before.get(), &expected_count);
    const auto* after_forms = physicality_descriptor_materialization_forms(after.get(), &actual_count);
    ASSERT_EQ(actual_count, bodies.size());
    ASSERT_EQ(actual_count, expected_count);
    for (size_t i = 0; i < actual_count; ++i) {
        EXPECT_TRUE(hash128_equals(&after_forms[i].descriptor_id, &before_forms[i].descriptor_id));
        EXPECT_TRUE(hash128_equals(&after_forms[i].view_id, &before_forms[i].view_id));
        EXPECT_EQ(after_forms[i].view_state, before_forms[i].view_state);
        EXPECT_EQ(after_forms[i].missing_first, before_forms[i].missing_first);
        EXPECT_EQ(after_forms[i].missing_count, before_forms[i].missing_count);
    }
    Stage before_stage(physicality_descriptor_materialization_take_stage(before.get()), intent_stage_free);
    Stage after_stage(physicality_descriptor_materialization_take_stage(after.get()), intent_stage_free);
    for (int table = 1; table <= 3; ++table) {
        size_t before_bytes = 0, after_bytes = 0;
        const auto* want = intent_stage_tuple_ptr(before_stage.get(), static_cast<intent_stage_table_t>(table), &before_bytes);
        const auto* got = intent_stage_tuple_ptr(after_stage.get(), static_cast<intent_stage_table_t>(table), &after_bytes);
        ASSERT_EQ(after_bytes, before_bytes);
        if (after_bytes != 0) {
            EXPECT_EQ(std::memcmp(got, want, after_bytes), 0);
        }
    }
}

TEST_F(PhysicalityDescriptorAdmission, CancelledCapturePublishesNothingAndPreservesBorrowedRows) {
    std::vector<Body> bodies;
    for (size_t i = 0; i < 64; ++i)
        bodies.push_back(composition({atom('a' + static_cast<uint32_t>(i % 26)),
                                      atom('A' + static_cast<uint32_t>(i / 26))}));
    auto original = stage(bodies);
    ASSERT_NE(original, nullptr);
    const intent_stage_t* source = original.get();
    const physicality_descriptor_limits_t limits{kBudget};
    DescriptorCancellationProbe count;
    physicality_descriptor_cancel_t cancellation{DescriptorCancellationProbe::requested, &count};
    physicality_descriptor_capture_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages_cancelable(&source, 1,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits, kBudget,
        &cancellation, &raw), PHYSICALITY_DESCRIPTOR_OK);
    Capture completed(raw, physicality_descriptor_capture_free);
    ASSERT_GT(count.calls, 100u);
    const size_t full_calls = count.calls;
    for (size_t stop : {size_t{1}, size_t{2}, bodies.size() + 4u, full_calls / 2, full_calls}) {
        SCOPED_TRACE(stop);
        DescriptorCancellationProbe probe{0, stop};
        cancellation.context = &probe;
        raw = nullptr;
        EXPECT_EQ(physicality_descriptor_capture_stages_cancelable(&source, 1,
            physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits, kBudget,
            &cancellation, &raw), PHYSICALITY_DESCRIPTOR_CANCELLED);
        EXPECT_EQ(raw, nullptr);
        EXPECT_EQ(probe.calls, stop);
    }
    for (size_t stop : {size_t{3}, size_t{8}}) {
        DescriptorCancellationProbe preflight{0, stop};
        cancellation.context = &preflight;
        size_t rows = 91, vertices = 92, logical = 93;
        EXPECT_EQ(physicality_descriptor_stages_preflight_cancelable(&source, 1, kBudget,
            &cancellation, &rows, &vertices, &logical), PHYSICALITY_DESCRIPTOR_CANCELLED);
        EXPECT_EQ(preflight.calls, stop);
        EXPECT_EQ(rows, 91u);
        EXPECT_EQ(vertices, 92u);
        EXPECT_EQ(logical, 93u);
    }
    auto retry = capture(source); // ordinary API still owns the same complete rows
    ASSERT_NE(retry, nullptr);
    size_t expected_count = 0, actual_count = 0;
    const auto* expected = physicality_descriptor_capture_inputs(completed.get(), &expected_count);
    const auto* actual = physicality_descriptor_capture_inputs(retry.get(), &actual_count);
    ASSERT_EQ(actual_count, bodies.size());
    ASSERT_EQ(actual_count, expected_count);
    const auto* observations = physicality_descriptor_capture_observations(retry.get(), nullptr);
    for (size_t i = 0; i < actual_count; ++i) {
        EXPECT_TRUE(hash128_equals(&actual[i].entity_id, &expected[i].entity_id));
        EXPECT_EQ(actual[i].trajectory_vertices, expected[i].trajectory_vertices);
        EXPECT_EQ(std::memcmp(actual[i].trajectory_xyzm, expected[i].trajectory_xyzm,
            actual[i].trajectory_vertices * 4 * sizeof(double)), 0);
        EXPECT_EQ(observations[i].observed_at_unix_us, 100 + static_cast<int64_t>(i));
    }
}

TEST_F(PhysicalityDescriptorAdmission, CancellationUnwindsMaterializationIncludingLateOutputThenRetryMatchesLegacy) {
    std::vector<Body> bodies;
    for (size_t i = 0; i < 64; ++i)
        bodies.push_back(composition({atom('a' + static_cast<uint32_t>(i % 26)),
                                      atom('A' + static_cast<uint32_t>(i / 26))}));
    auto original = stage(bodies);
    auto captured = capture(original.get());
    ASSERT_NE(captured, nullptr);
    const intent_stage_t* provider = original.get();
    const auto sources = witnesses(bodies.size());
    DescriptorCancellationProbe count;
    physicality_descriptor_cancel_t cancellation{DescriptorCancellationProbe::requested, &count};
    physicality_descriptor_materialization_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_materialize_cancelable(captured.get(), vocabulary.get(),
        &provider, 1, &provider, 1, nullptr, 0, sources.data(), sources.size(),
        &kSource, 200, kBudget, &cancellation, &raw), PHYSICALITY_DESCRIPTOR_OK);
    Materialization completed(raw, physicality_descriptor_materialization_free);
    const size_t full_calls = count.calls;
    ASSERT_GT(full_calls, 100u);
    // The last checkpoint is after the generated stage is attached to its
    // result owner. CANCELLED must destroy that stage as well as transient RAII.
    for (size_t stop : {size_t{1}, size_t{100}, full_calls / 2, full_calls}) {
        SCOPED_TRACE(stop);
        DescriptorCancellationProbe probe{0, stop};
        cancellation.context = &probe;
        raw = nullptr;
        EXPECT_EQ(physicality_descriptor_materialize_cancelable(captured.get(), vocabulary.get(),
            &provider, 1, &provider, 1, nullptr, 0, sources.data(), sources.size(),
            &kSource, 200, kBudget, &cancellation, &raw), PHYSICALITY_DESCRIPTOR_CANCELLED);
        EXPECT_EQ(raw, nullptr);
        EXPECT_EQ(probe.calls, stop);
    }
    Materialization retry(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {provider}, {provider}, {}, sources, retry), PHYSICALITY_DESCRIPTOR_OK);
    size_t expected_count = 0, actual_count = 0;
    const auto* expected = physicality_descriptor_materialization_forms(completed.get(), &expected_count);
    const auto* actual = physicality_descriptor_materialization_forms(retry.get(), &actual_count);
    ASSERT_EQ(actual_count, expected_count);
    ASSERT_EQ(actual_count, bodies.size());
    for (size_t i = 0; i < actual_count; ++i) {
        EXPECT_TRUE(hash128_equals(&actual[i].descriptor_id, &expected[i].descriptor_id));
        EXPECT_TRUE(hash128_equals(&actual[i].view_id, &expected[i].view_id));
        EXPECT_EQ(actual[i].view_state, expected[i].view_state);
        EXPECT_EQ(actual[i].missing_first, expected[i].missing_first);
        EXPECT_EQ(actual[i].missing_count, expected[i].missing_count);
    }
    Stage expected_stage(physicality_descriptor_materialization_take_stage(completed.get()), intent_stage_free);
    Stage actual_stage(physicality_descriptor_materialization_take_stage(retry.get()), intent_stage_free);
    ASSERT_NE(expected_stage, nullptr);
    ASSERT_NE(actual_stage, nullptr);
    for (const auto table : {INTENT_STAGE_TABLE_ENTITIES, INTENT_STAGE_TABLE_PHYSICALITIES,
                             INTENT_STAGE_TABLE_ATTESTATIONS}) {
        size_t expected_bytes = 0, actual_bytes = 0;
        const auto* want = intent_stage_tuple_ptr(expected_stage.get(), table, &expected_bytes);
        const auto* got = intent_stage_tuple_ptr(actual_stage.get(), table, &actual_bytes);
        ASSERT_EQ(actual_bytes, expected_bytes);
        if (actual_bytes != 0) EXPECT_EQ(std::memcmp(got, want, actual_bytes), 0);
    }
}

TEST_F(PhysicalityDescriptorAdmission, ActualByteCarriersUsePinnedBasisAndStoredContentTakesPrecedence) {
    const auto& byte_basis = vocabulary->byte_basis;
    std::vector<Body> bytes(2);
    for (size_t i = 0; i < bytes.size(); ++i) {
        bytes[i].value.type = 1;
        bytes[i].value.alignment_residual_is_null = bytes[i].value.source_dim_is_null = 1;
        bytes[i].value.entity_id = byte_basis.atoms[i].id;
        std::copy_n(byte_basis.atoms[i].coord, 4, bytes[i].value.coord);
        bytes[i].value.hilbert_index = byte_basis.atoms[i].hilbert;
    }
    const auto parent = composition(bytes);
    auto original = stage({parent});
    auto captured = capture(original.get());
    Materialization basis(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, {bytes[0].value.entity_id, bytes[1].value.entity_id}, witnesses(1), basis),
        PHYSICALITY_DESCRIPTOR_OK);
    const auto basis_form = form(basis);
    auto changed = bytes[0];
    changed.value.coord[0] += 0.125;
    hilbert4d_encode(changed.value.coord, &changed.value.hilbert_index);
    auto stored = stage({changed});
    Materialization current(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {stored.get()}, {}, {}, witnesses(1), current), PHYSICALITY_DESCRIPTOR_OK);
    const auto current_form = form(current);
    EXPECT_TRUE(hash128_equals(&basis_form.descriptor_id, &current_form.descriptor_id));
    EXPECT_FALSE(hash128_equals(&basis_form.view_id, &current_form.view_id));
    const auto saved = vocabulary->byte_basis;
    vocabulary->byte_basis.atoms[0].coord[0] += 0.125;
    EXPECT_EQ(run(captured, {}, {}, {}, witnesses(1), current), PHYSICALITY_DESCRIPTOR_MISSING_FLOOR);
    EXPECT_EQ(current, nullptr);
    vocabulary->byte_basis = {};
    EXPECT_EQ(run(captured, {}, {}, {}, witnesses(1), current), PHYSICALITY_DESCRIPTOR_MISSING_FLOOR);
    EXPECT_EQ(current, nullptr);
    vocabulary->byte_basis = saved;
}

TEST_F(PhysicalityDescriptorAdmission, TransitiveProviderChangeKeepsExactDescriptorAndChangesRootView) {
    const auto c = composition({atom('a'), atom('b')});
    const auto b = composition({c, atom('x')});
    const auto a = composition({b, atom('y')});
    auto changed_c = c;
    changed_c.value.coord[0] += 0.125;
    hilbert4d_encode(changed_c.value.coord, &changed_c.value.hilbert_index);
    auto source = stage({a});
    auto first_current = stage({b, c});
    auto second_current = stage({b, changed_c});
    auto captured = capture(source.get());
    ASSERT_NE(captured, nullptr);
    Materialization first(nullptr, physicality_descriptor_materialization_free);
    Materialization second(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {first_current.get()}, {}, {}, witnesses(1), first), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(captured, {second_current.get()}, {}, {}, witnesses(1), second), PHYSICALITY_DESCRIPTOR_OK);
    const auto first_form = form(first);
    const auto second_form = form(second);
    EXPECT_TRUE(hash128_equals(&first_form.descriptor_id, &second_form.descriptor_id));
    EXPECT_FALSE(hash128_equals(&first_form.view_id, &second_form.view_id));
    Stage first_stage(physicality_descriptor_materialization_take_stage(first.get()), intent_stage_free);
    Stage second_stage(physicality_descriptor_materialization_take_stage(second.get()), intent_stage_free);
    ASSERT_NE(first_stage, nullptr);
    ASSERT_NE(second_stage, nullptr);
    EXPECT_GT(intent_stage_physicality_count(first_stage.get()), 0u);
    EXPECT_GT(intent_stage_physicality_count(second_stage.get()), 0u);
}

TEST_F(PhysicalityDescriptorAdmission, RootIdentityAndViewIgnoreUnrelatedBatchNeighborsAndStageOrder) {
    const auto c = composition({atom('a'), atom('b')});
    const auto b = composition({c, atom('x')});
    const auto a = composition({b, atom('y')});
    const auto neighbor = composition({atom('q'), atom('z')});
    auto alone = stage({a});
    auto mixed = stage({neighbor, a});
    auto first_current = stage({b, c});
    auto second_current = stage({neighbor, c, b});
    auto captured_alone = capture(alone.get());
    auto captured_mixed = capture(mixed.get());
    Materialization first(nullptr, physicality_descriptor_materialization_free);
    Materialization second(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured_alone, {first_current.get()}, {}, {}, witnesses(1), first), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(captured_mixed, {second_current.get()}, {}, {}, witnesses(2), second), PHYSICALITY_DESCRIPTOR_OK);
    const auto first_form = form(first);
    const auto second_form = form(second, 1);
    EXPECT_TRUE(hash128_equals(&first_form.descriptor_id, &second_form.descriptor_id));
    EXPECT_TRUE(hash128_equals(&first_form.view_id, &second_form.view_id));
}

TEST_F(PhysicalityDescriptorAdmission, FrontierRequiresCheckedAbsenceAndTheExplicitAdmittedWinner) {
    const auto c = composition({atom('a'), atom('b')});
    const auto b = composition({c, atom('x')});
    const auto a = composition({b, atom('y')});
    auto original_stage = stage({a});
    auto admitted_stage = stage({b, c});
    auto only_b = stage({b});
    auto captured = capture(original_stage.get());
    Materialization result(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {admitted_stage.get()}, {}, witnesses(1), result), PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    size_t count = 0;
    const auto* pending = physicality_descriptor_materialization_pending(result.get(), &count);
    ASSERT_EQ(count, 2u); // Includes the supplied winner's own unresolved child.
    EXPECT_TRUE(std::any_of(pending, pending + count, [&](const hash128_t& id) {
        return hash128_equals(&id, &b.value.entity_id);
    }));
    EXPECT_EQ(physicality_descriptor_materialization_take_stage(result.get()), nullptr);
    ASSERT_EQ(run(captured, {only_b.get()}, {}, {}, witnesses(1), result), PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    pending = physicality_descriptor_materialization_pending(result.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_TRUE(hash128_equals(pending, &c.value.entity_id));
    const std::vector<hash128_t> missing{b.value.entity_id, c.value.entity_id};
    ASSERT_EQ(run(captured, {}, {admitted_stage.get()}, missing, witnesses(1), result), PHYSICALITY_DESCRIPTOR_OK);
    auto extra_raw = stage({a, b, c});
    auto raw_capture = capture(extra_raw.get());
    ASSERT_EQ(run(raw_capture, {}, {}, missing, witnesses(3), result), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(form(result, 0).view_state, PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE);
    EXPECT_EQ(form(result, 1).view_state, PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE);
    EXPECT_EQ(form(result, 2).view_state, PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE);
    EXPECT_EQ(run(captured, {admitted_stage.get()}, {}, missing, witnesses(1), result), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(result, nullptr);
}

TEST_F(PhysicalityDescriptorAdmission, MixedWriterStagesSelectFirstContentPlacementAndRetainAllRawForms) {
    const auto child = composition({atom('A'), atom('B')});
    auto alternate = child;
    alternate.value.coord[0] += 0.125;
    hilbert4d_encode(alternate.value.coord, &alternate.value.hilbert_index);
    const auto parent = composition({child, atom('C')});
    auto projection = parent;
    projection.value.type = 3;
    auto parse = parent;
    parse.value.type = 8;
    auto first = stage({parent, projection, parse, child});
    auto second = stage({alternate, child});
    auto all = stage({parent, projection, parse, child, alternate, child});
    auto source = capture(all.get());
    auto current = stage({child});
    auto changed_current = stage({alternate});
    Materialization fallback(nullptr, physicality_descriptor_materialization_free);
    Materialization replay(nullptr, physicality_descriptor_materialization_free);
    Materialization reversed(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(source, {}, {first.get(), second.get()}, {}, witnesses(6), fallback),
        PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    ASSERT_EQ(run(source, {}, {first.get(), second.get()}, {child.value.entity_id}, witnesses(6), fallback),
        PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(source, {current.get()}, {}, {}, witnesses(6), replay), PHYSICALITY_DESCRIPTOR_OK);
    for (size_t i = 0; i < 6; ++i) {
        const auto admitted = form(fallback, i), stored = form(replay, i);
        EXPECT_TRUE(hash128_equals(&admitted.descriptor_id, &stored.descriptor_id));
        EXPECT_TRUE(hash128_equals(&admitted.view_id, &stored.view_id));
    }
    const auto first_form = form(fallback, 3), alternate_form = form(fallback, 4), repeated_form = form(fallback, 5);
    EXPECT_FALSE(hash128_equals(&first_form.descriptor_id, &alternate_form.descriptor_id));
    EXPECT_TRUE(hash128_equals(&first_form.descriptor_id, &repeated_form.descriptor_id));
    ASSERT_EQ(run(source, {}, {second.get(), first.get()}, {child.value.entity_id}, witnesses(6), reversed),
        PHYSICALITY_DESCRIPTOR_OK);
    const auto original_parent = form(fallback), changed_parent = form(reversed);
    EXPECT_TRUE(hash128_equals(&original_parent.descriptor_id, &changed_parent.descriptor_id));
    EXPECT_FALSE(hash128_equals(&original_parent.view_id, &changed_parent.view_id));
    ASSERT_EQ(run(source, {changed_current.get()}, {}, {}, witnesses(6), replay), PHYSICALITY_DESCRIPTOR_OK);
    const auto current_parent = form(replay);
    EXPECT_TRUE(hash128_equals(&changed_parent.view_id, &current_parent.view_id));
    Stage generated(physicality_descriptor_materialization_take_stage(fallback.get()), intent_stage_free);
    EXPECT_EQ(intent_stage_attestation_count(generated.get()), 5u);
}

TEST_F(PhysicalityDescriptorAdmission, ReleasedCaptureKeepsExactMaterializationAndFiniteCoexistence) {
    const auto child = composition({atom('A'), atom('B')});
    const auto parent = composition({child, atom('C')});
    auto projection = parent;
    projection.value.type = 3;
    auto alternate = child;
    alternate.value.coord[0] += 0.125;
    hilbert4d_encode(alternate.value.coord, &alternate.value.hilbert_index);
    auto original = stage({parent, projection, parent}, {-1234567, 0, 1700000000123456});
    auto retained = capture(original.get());
    auto released = capture(original.get());
    ASSERT_NE(retained, nullptr);
    ASSERT_NE(released, nullptr);
    const size_t capture_peak = physicality_descriptor_capture_peak_bytes(released.get());
    const size_t plan_bytes = physicality_descriptor_plan_bytes(physicality_descriptor_capture_plan(released.get()));
    ASSERT_GT(plan_bytes, 0u);
    ASSERT_EQ(physicality_descriptor_capture_release_plan(released.get()), plan_bytes);
    const size_t capture_bytes = physicality_descriptor_capture_bytes(released.get());
    EXPECT_EQ(physicality_descriptor_capture_bytes(retained.get()), capture_bytes + plan_bytes);
    auto current = stage({child});
    auto admitted = stage({child, projection, alternate});
    auto sources = witnesses(3);
    sources[2].source_unit_id = hash128_t{1234, 5678};
    struct Selection {
        std::vector<const intent_stage_t*> current;
        std::vector<const intent_stage_t*> admitted;
        std::vector<hash128_t> missing;
        physicality_descriptor_status_t status;
    };
    const std::array<Selection, 4> selections{{
        {{}, {admitted.get()}, {}, PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER},
        {{current.get()}, {admitted.get()}, {}, PHYSICALITY_DESCRIPTOR_OK},
        {{}, {admitted.get()}, {child.value.entity_id}, PHYSICALITY_DESCRIPTOR_OK},
        {{}, {}, {child.value.entity_id}, PHYSICALITY_DESCRIPTOR_OK}
    }};
    for (size_t selection = 0; selection < selections.size(); ++selection) {
        SCOPED_TRACE(selection);
        const auto& selected = selections[selection];
        Materialization before(nullptr, physicality_descriptor_materialization_free);
        Materialization after(nullptr, physicality_descriptor_materialization_free);
        ASSERT_EQ(run(retained, selected.current, selected.admitted, selected.missing, sources, before),
            selected.status);
        ASSERT_NE(before, nullptr);
        const size_t native_peak = physicality_descriptor_materialization_peak_bytes(before.get());
        // Each independent execution accounts for source capture and the native
        // materializer together. The original capture peak is still admitted;
        // no assertion assumes that old peak-minus-one must fail after growth.
        ASSERT_LE(native_peak, kBudget - capture_bytes);
        const size_t grant = std::max(capture_peak, capture_bytes + native_peak);
        physicality_descriptor_materialization_diagnostics_t diagnostics{};
        physicality_descriptor_materialization_t* after_raw = nullptr;
        const auto diagnosed_status = physicality_descriptor_materialize_diagnosed_cancelable(
            released.get(), vocabulary.get(), selected.current.data(), selected.current.size(),
            selected.admitted.data(), selected.admitted.size(),
            selected.missing.data(), selected.missing.size(), sources.data(), sources.size(),
            &kSource, 200, grant - capture_bytes, nullptr, &diagnostics, &after_raw);
        after.reset(after_raw);
        ASSERT_EQ(diagnosed_status, selected.status);
        EXPECT_EQ(diagnostics.status, selected.status);
        EXPECT_EQ(diagnostics.refusal_kind, PHYSICALITY_MATERIALIZATION_REFUSAL_NONE);
        if (selected.status == PHYSICALITY_DESCRIPTOR_OK) {
            EXPECT_EQ(diagnostics.phase, PHYSICALITY_MATERIALIZATION_COMPLETE);
            // The combined plan was fully authenticated before its owners
            // retired; copied forms and complete E/P/A bytes are checked below.
            EXPECT_GT(diagnostics.plan.node_count, 0u);
            EXPECT_GE(diagnostics.released_before_serialization_bytes,
                diagnostics.plan.retained_bytes);
            EXPECT_GT(diagnostics.serialization_entry_bytes, 0u);
            EXPECT_LE(diagnostics.peak_bytes, diagnostics.maximum_bytes);
        } else {
            EXPECT_EQ(diagnostics.phase, PHYSICALITY_MATERIALIZATION_PROVIDER_INDEX);
            EXPECT_EQ(diagnostics.released_before_serialization_bytes, 0u);
        }
        ASSERT_NE(after, nullptr);
        // Both OK and NEEDS_PROVIDER diagnostics describe the published owner
        // after local temporaries retire, in the documented header-free scope.
        const size_t result_header = (grant - capture_bytes) - diagnostics.maximum_bytes;
        EXPECT_GT(result_header, 0u);
        EXPECT_EQ(diagnostics.retained_bytes + result_header,
            physicality_descriptor_materialization_bytes(after.get()));
        EXPECT_EQ(diagnostics.peak_bytes + result_header,
            physicality_descriptor_materialization_peak_bytes(after.get()));
        EXPECT_LE(capture_bytes + physicality_descriptor_materialization_peak_bytes(after.get()), grant);
        EXPECT_LE(capture_peak, grant);
        EXPECT_EQ(physicality_descriptor_capture_peak_bytes(released.get()), capture_peak);
        EXPECT_NE(physicality_descriptor_capture_plan(retained.get()), nullptr);
        EXPECT_EQ(physicality_descriptor_capture_plan(released.get()), nullptr);
        size_t before_count = 0, after_count = 0;
        const auto* before_pending = physicality_descriptor_materialization_pending(before.get(), &before_count);
        const auto* after_pending = physicality_descriptor_materialization_pending(after.get(), &after_count);
        ASSERT_EQ(before_count, after_count);
        if (selected.status == PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER) ASSERT_GT(before_count, 0u);
        for (size_t i = 0; i < before_count; ++i)
            EXPECT_TRUE(hash128_equals(&before_pending[i], &after_pending[i]));
        const auto* before_missing = physicality_descriptor_materialization_missing(before.get(), &before_count);
        const auto* after_missing = physicality_descriptor_materialization_missing(after.get(), &after_count);
        ASSERT_EQ(before_count, after_count);
        if (selection == 3u) ASSERT_GT(before_count, 0u);
        for (size_t i = 0; i < before_count; ++i)
            EXPECT_TRUE(hash128_equals(&before_missing[i], &after_missing[i]));
        const auto* before_forms = physicality_descriptor_materialization_forms(before.get(), &before_count);
        const auto* after_forms = physicality_descriptor_materialization_forms(after.get(), &after_count);
        ASSERT_EQ(before_count, after_count);
        if (selected.status == PHYSICALITY_DESCRIPTOR_OK) ASSERT_EQ(before_count, 3u);
        for (size_t i = 0; i < before_count; ++i) {
            EXPECT_TRUE(hash128_equals(&before_forms[i].descriptor_id, &after_forms[i].descriptor_id));
            EXPECT_EQ(before_forms[i].view_state, after_forms[i].view_state);
            if (before_forms[i].view_state == PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE)
                EXPECT_TRUE(hash128_equals(&before_forms[i].view_id, &after_forms[i].view_id));
            EXPECT_EQ(before_forms[i].missing_first, after_forms[i].missing_first);
            EXPECT_EQ(before_forms[i].missing_count, after_forms[i].missing_count);
        }
        Stage before_stage(physicality_descriptor_materialization_take_stage(before.get()), intent_stage_free);
        Stage after_stage(physicality_descriptor_materialization_take_stage(after.get()), intent_stage_free);
        if (selected.status == PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER) {
            EXPECT_EQ(before_stage, nullptr);
            EXPECT_EQ(after_stage, nullptr);
            continue;
        }
        ASSERT_NE(before_stage, nullptr);
        ASSERT_NE(after_stage, nullptr);
        EXPECT_EQ(intent_stage_attestation_count(before_stage.get()), 3u);
        // Compare actual COPY bytes, including all timestamps and occurrences;
        // the semantic digest alone deliberately excludes observation time.
        for (const auto table : {INTENT_STAGE_TABLE_ENTITIES, INTENT_STAGE_TABLE_PHYSICALITIES,
                                INTENT_STAGE_TABLE_ATTESTATIONS}) {
            size_t before_bytes = 0, after_bytes = 0;
            const auto* before_data = intent_stage_tuple_ptr(before_stage.get(), table, &before_bytes);
            const auto* after_data = intent_stage_tuple_ptr(after_stage.get(), table, &after_bytes);
            ASSERT_EQ(before_bytes, after_bytes);
            ASSERT_GT(before_bytes, 0u);
            EXPECT_EQ(std::memcmp(before_data, after_data, before_bytes), 0);
        }
    }
}

TEST_F(PhysicalityDescriptorAdmission, ProvidersAuthenticateEveryBodyAndCurrentSelectionRemainsStrict) {
    const auto child = composition({atom('A'), atom('B')});
    const auto parent = composition({child, atom('C')});
    auto alternate = child;
    alternate.value.coord[0] += 0.125;
    hilbert4d_encode(alternate.value.coord, &alternate.value.hilbert_index);
    auto projection = parent;
    projection.value.type = 3;
    auto original = stage({parent});
    auto source = capture(original.get());
    auto mixed = stage({child, projection});
    auto conflicting = stage({child, alternate});
    Materialization result(nullptr, physicality_descriptor_materialization_free);
    EXPECT_EQ(run(source, {mixed.get()}, {}, {}, witnesses(1), result), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(result, nullptr);
    EXPECT_EQ(run(source, {conflicting.get()}, {}, {}, witnesses(1), result), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(result, nullptr);
    // A typed row excluded from Content selection is still authenticated.
    projection.value.coord[0] = std::numeric_limits<double>::quiet_NaN();
    auto malformed = stage({child, projection});
    EXPECT_EQ(run(source, {}, {malformed.get()}, {child.value.entity_id}, witnesses(1), result),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(result, nullptr);
}

TEST_F(PhysicalityDescriptorAdmission, RepeatedCompositeCarrierRequiresProviderAndReusesAdmittedBodyAsCurrent) {
    const auto child = composition({atom('A'), atom('B')});
    auto parent = composition({child, child});
    ASSERT_EQ(std::memcmp(parent.value.coord, child.value.coord, sizeof(parent.value.coord)), 0);
    ASSERT_EQ(std::memcmp(&parent.value.hilbert_index, &child.value.hilbert_index,
        sizeof(parent.value.hilbert_index)), 0);
    const hash128_t repeated[] = {child.value.entity_id, child.value.entity_id};
    size_t stored = 0;
    ASSERT_EQ(trajectory_build_rle(repeated, 2, parent.trajectory.data(), &stored), 0);
    ASSERT_EQ(stored, 1u);
    parent.trajectory.resize(stored * 4u);
    parent.value.trajectory_vertices = stored;
    ASSERT_EQ(parent.value.n_constituents, 2);
    auto raw = stage({parent});
    auto winner = stage({child});
    auto captured = capture(raw.get());
    ASSERT_NE(captured, nullptr);
    Materialization pending(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {winner.get()}, {}, witnesses(1), pending),
        PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    size_t count = 0;
    const auto* wanted = physicality_descriptor_materialization_pending(pending.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_TRUE(hash128_equals(wanted, &child.value.entity_id));
    EXPECT_EQ(physicality_descriptor_materialization_take_stage(pending.get()), nullptr);
    Materialization admitted(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {winner.get()}, {child.value.entity_id}, witnesses(1), admitted),
        PHYSICALITY_DESCRIPTOR_OK);
    Materialization current(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {winner.get()}, {}, {}, witnesses(1), current),
        PHYSICALITY_DESCRIPTOR_OK);
    const auto admitted_form = form(admitted);
    const auto current_form = form(current);
    EXPECT_TRUE(hash128_equals(&admitted_form.descriptor_id, &current_form.descriptor_id));
    EXPECT_TRUE(hash128_equals(&admitted_form.view_id, &current_form.view_id));
    ASSERT_EQ(run(captured, {}, {}, {child.value.entity_id}, witnesses(1), pending),
        PHYSICALITY_DESCRIPTOR_OK);
    const auto unavailable_form = form(pending);
    EXPECT_EQ(unavailable_form.view_state, PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE);
    EXPECT_TRUE(hash128_equals(&unavailable_form.descriptor_id, &admitted_form.descriptor_id));
}

TEST_F(PhysicalityDescriptorAdmission, ExactSelfReferenceCycleTerminatesWithoutGeneratedFeedback) {
    const auto content = composition({atom('c'), atom('d')});
    Body self = composition({content});
    auto original = stage({self});
    auto current = stage({self});
    auto captured = capture(original.get());
    Materialization first(nullptr, physicality_descriptor_materialization_free);
    Materialization duplicate(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {current.get()}, {}, {}, witnesses(1), first), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(captured, {current.get(), current.get()}, {}, {}, witnesses(1), duplicate), PHYSICALITY_DESCRIPTOR_OK);
    const auto x = form(first);
    const auto y = form(duplicate);
    EXPECT_TRUE(hash128_equals(&x.descriptor_id, &y.descriptor_id));
    EXPECT_TRUE(hash128_equals(&x.view_id, &y.view_id));
    size_t pending = 1;
    physicality_descriptor_materialization_pending(first.get(), &pending);
    EXPECT_EQ(pending, 0u);
    Stage generated(physicality_descriptor_materialization_take_stage(first.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    EXPECT_GT(intent_stage_physicality_count(generated.get()), 0u);
    EXPECT_LT(intent_stage_physicality_count(generated.get()), 200u);
}

struct Field { const uint8_t* bytes; size_t size; };

uint64_t big_word(const uint8_t* bytes, size_t count) {
    uint64_t value = 0;
    for (size_t i = 0; i < count; ++i) value = (value << 8u) | bytes[i];
    return value;
}

bool next_row(const uint8_t* data, size_t bytes, size_t& at, std::vector<Field>& fields) {
    if (at > bytes || bytes - at < 2u) return false;
    const size_t count = big_word(data + at, 2);
    at += 2;
    fields.clear();
    for (size_t i = 0; i < count; ++i) {
        if (bytes - at < 4u) return false;
        const uint32_t width = static_cast<uint32_t>(big_word(data + at, 4));
        at += 4;
        if (width == UINT32_MAX) { fields.push_back({nullptr, 0}); continue; }
        if (width > bytes - at) return false;
        fields.push_back({data + at, width});
        at += width;
    }
    return true;
}

TEST_F(PhysicalityDescriptorAdmission, DuplicateSourceUnitWitnessesKeepOneObservationAndLatestTime) {
    const auto a = composition({atom('a'), atom('b')});
    auto original = stage({a, a, a}, {3, 9, 5});
    auto captured = capture(original.get());
    auto sources = witnesses(3);
    sources[2].source_unit_id.lo += 1;
    Materialization result(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, {}, sources, result), PHYSICALITY_DESCRIPTOR_OK);
    const auto descriptor = form(result).descriptor_id;
    Stage generated(physicality_descriptor_materialization_take_stage(result.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    ASSERT_EQ(intent_stage_attestation_count(generated.get()), 2u);
    // Capture validates canonical Content identities from the actual emitted
    // physicalities before the attestation context is interpreted below.
    auto emitted = capture(generated.get());
    ASSERT_NE(emitted, nullptr);
    size_t body_count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(emitted.get(), &body_count);
    const auto* basis = physicality_descriptor_vocabulary_basis(vocabulary.get());
    const char* schema_names[] = {"PhysicalitySourceUnitContextV1",
        "PhysicalitySourceIdentifierV1", "PhysicalitySourceUnitReceiptV1"};
    std::array<hash128_t, 3> schemas{};
    for (size_t i = 0; i < schemas.size(); ++i)
        ASSERT_EQ(laplace_content_root_id(reinterpret_cast<const uint8_t*>(schema_names[i]),
            std::strlen(schema_names[i]), &schemas[i]), 0);
    std::vector<hash128_t> entities;
    size_t entity_bytes = 0, entity_at = 0;
    const auto* entity_data = intent_stage_tuple_ptr(generated.get(), INTENT_STAGE_TABLE_ENTITIES, &entity_bytes);
    std::vector<Field> fields;
    while (entity_at < entity_bytes) {
        ASSERT_TRUE(next_row(entity_data, entity_bytes, entity_at, fields));
        ASSERT_EQ(fields.size(), 4u);
        ASSERT_EQ(fields[0].size, sizeof(hash128_t));
        hash128_t id;
        std::memcpy(&id, fields[0].bytes, sizeof(id));
        entities.push_back(id);
    }
    const auto find_body = [&](const hash128_t& id) {
        return std::find_if(bodies, bodies + body_count, [&](const auto& body) {
            return hash128_equals(&id, &body.entity_id);
        });
    };
    const auto has_entity = [&](const hash128_t& id) {
        return std::any_of(entities.begin(), entities.end(), [&](const hash128_t& entity) {
            return hash128_equals(&id, &entity);
        });
    };
    size_t bytes = 0, at = 0, rows = 0;
    const auto* data = intent_stage_tuple_ptr(generated.get(), INTENT_STAGE_TABLE_ATTESTATIONS, &bytes);
    std::array<bool, 2> seen_units{};
    while (at < bytes) {
        ASSERT_TRUE(next_row(data, bytes, at, fields));
        ASSERT_EQ(fields.size(), 14u);
        ASSERT_EQ(fields[3].size, sizeof(hash128_t));
        ASSERT_EQ(fields[4].size, sizeof(hash128_t));
        ASSERT_EQ(fields[5].size, sizeof(hash128_t));
        EXPECT_EQ(std::memcmp(fields[3].bytes, &descriptor, sizeof(descriptor)), 0);
        EXPECT_EQ(std::memcmp(fields[4].bytes, &kSource, sizeof(kSource)), 0);
        hash128_t context;
        std::memcpy(&context, fields[5].bytes, sizeof(context));
        EXPECT_FALSE(hash128_equals(&context, &kUnit));
        EXPECT_FALSE(hash128_equals(&context, &sources[2].source_unit_id));
        ASSERT_TRUE(has_entity(context));
        const auto* context_body = find_body(context);
        ASSERT_NE(context_body, bodies + body_count);
        ASSERT_EQ(context_body->type, 1);
        ASSERT_EQ(context_body->n_constituents, 3);
        std::array<hash128_t, 3> context_children{};
        ASSERT_EQ(trajectory_constituents(context_body->trajectory_xyzm,
            context_body->trajectory_vertices, context_children.data(), context_children.size()), 3);
        EXPECT_TRUE(hash128_equals(&context_children[0], &schemas[0]));
        std::array<hash128_t, 2> decoded{};
        for (size_t identifier = 0; identifier < decoded.size(); ++identifier) {
            ASSERT_TRUE(has_entity(context_children[identifier + 1]));
            const auto* identifier_body = find_body(context_children[identifier + 1]);
            ASSERT_NE(identifier_body, bodies + body_count);
            ASSERT_EQ(identifier_body->type, 1);
            ASSERT_EQ(identifier_body->n_constituents, 17);
            std::array<hash128_t, 17> identifier_children{};
            ASSERT_EQ(trajectory_constituents(identifier_body->trajectory_xyzm,
                identifier_body->trajectory_vertices, identifier_children.data(), identifier_children.size()), 17);
            EXPECT_TRUE(hash128_equals(&identifier_children[0], &schemas[identifier + 1]));
            auto* octets = reinterpret_cast<uint8_t*>(&decoded[identifier]);
            for (size_t octet = 0; octet < sizeof(hash128_t); ++octet) {
                size_t value = 0;
                while (value < 256u && !hash128_equals(&identifier_children[octet + 1],
                    &basis->byte_numbers[value])) ++value;
                ASSERT_LT(value, 256u);
                octets[octet] = static_cast<uint8_t>(value);
            }
        }
        EXPECT_TRUE(hash128_equals(&decoded[0], &kSource));
        const bool first_unit = hash128_equals(&decoded[1], &kUnit);
        EXPECT_TRUE(first_unit || hash128_equals(&decoded[1], &sources[2].source_unit_id));
        EXPECT_FALSE(seen_units[first_unit ? 0 : 1]);
        seen_units[first_unit ? 0 : 1] = true;
        EXPECT_EQ(big_word(fields[6].bytes, fields[6].size), 2u);
        EXPECT_EQ(big_word(fields[8].bytes, fields[8].size), 1u);
        const uint64_t encoded = big_word(fields[7].bytes, fields[7].size);
        int64_t pg_time;
        std::memcpy(&pg_time, &encoded, sizeof(pg_time));
        EXPECT_EQ(pg_time + INTENT_STAGE_PG_EPOCH_UNIX_US, first_unit ? 9 : 5);
        ++rows;
    }
    EXPECT_EQ(rows, 2u);
    EXPECT_TRUE(seen_units[0]);
    EXPECT_TRUE(seen_units[1]);
}

TEST_F(PhysicalityDescriptorAdmission, RejectsInvalidOrConflictingSourcePriorWithoutPublishingStage) {
    const auto a = composition({atom('a'), atom('b')});
    auto original = stage({a, a});
    auto captured = capture(original.get());
    Materialization result(nullptr, physicality_descriptor_materialization_free);
    for (const double invalid : {-0.01, 1.01, std::numeric_limits<double>::quiet_NaN()}) {
        auto sources = witnesses(2);
        sources[0].source_trust = invalid;
        EXPECT_EQ(run(captured, {}, {}, {}, sources, result), PHYSICALITY_DESCRIPTOR_INVALID);
        EXPECT_EQ(result, nullptr);
    }
    auto conflicting = witnesses(2);
    conflicting[1].source_trust = 0.2;
    EXPECT_EQ(run(captured, {}, {}, {}, conflicting, result), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(result, nullptr);
    EXPECT_EQ(run(captured, {}, {}, {}, witnesses(1), result), PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(result, nullptr);
}

TEST_F(PhysicalityDescriptorAdmission, RetainedReceiptTracksStageTransferAndBudgetFailurePublishesNothing) {
    const auto a = composition({atom('a'), atom('b')});
    auto original = stage({a});
    auto captured = capture(original.get());
    Materialization result(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, {}, witnesses(1), result), PHYSICALITY_DESCRIPTOR_OK);
    const size_t before = physicality_descriptor_materialization_bytes(result.get());
    const size_t peak = physicality_descriptor_materialization_peak_bytes(result.get());
    EXPECT_GE(peak, before);
    EXPECT_LE(peak, kBudget);
    Stage generated(physicality_descriptor_materialization_take_stage(result.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    EXPECT_EQ(before - physicality_descriptor_materialization_bytes(result.get()),
        intent_stage_memory_bytes(generated.get()));
    EXPECT_EQ(physicality_descriptor_materialization_take_stage(result.get()), nullptr);
    const size_t after = physicality_descriptor_materialization_bytes(result.get());
    EXPECT_LT(after, before);
    EXPECT_EQ(physicality_descriptor_materialization_peak_bytes(result.get()), peak);
    result.reset();
    hash128_t digest{};
    EXPECT_EQ(intent_stage_semantic_digest(generated.get(), &digest), 0);
    EXPECT_EQ(run(captured, {}, {}, {}, witnesses(1), result, 1), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(result, nullptr);
}

TEST_F(PhysicalityDescriptorAdmission, ExactBodyReadsBackFromActualGeneratedCompositionTrajectories) {
    auto original_body = composition({atom('a'), atom('b')});
    original_body.value.type = 3;
    original_body.value.coord[0] = -0.0;
    original_body.value.hilbert_index.bytes[15] ^= 0x5a;
    original_body.value.alignment_residual_is_null = 0;
    original_body.value.alignment_residual = -0.0;
    original_body.value.source_dim_is_null = 0;
    original_body.value.source_dim = 17;
    auto original = stage({original_body});
    auto captured = capture(original.get());
    Materialization result(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, {}, witnesses(1), result), PHYSICALITY_DESCRIPTOR_OK);
    const hash128_t descriptor = form(result).descriptor_id;
    Stage generated(physicality_descriptor_materialization_take_stage(result.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    auto emitted = capture(generated.get());
    ASSERT_NE(emitted, nullptr);
    size_t emitted_count = 0;
    const auto* rows = physicality_descriptor_capture_inputs(emitted.get(), &emitted_count);
    std::vector<physicality_descriptor_node_t> nodes;
    std::vector<hash128_t> children;
    for (size_t i = 0; i < emitted_count; ++i) {
        ASSERT_TRUE(rows[i].type == 1 || rows[i].type == PHYSICALITY_DESCRIPTOR_RETENTION_TYPE);
        ASSERT_GE(rows[i].n_constituents, 2);
        const size_t first = children.size();
        const size_t count = static_cast<size_t>(rows[i].n_constituents);
        children.resize(first + count);
        ASSERT_EQ(trajectory_constituents(rows[i].trajectory_xyzm,
            rows[i].trajectory_vertices, children.data() + first, count), rows[i].n_constituents);
        nodes.push_back({rows[i].entity_id, first, count});
    }
    physicality_descriptor_readback_t* raw = nullptr;
    const physicality_descriptor_limits_t limits{kBudget};
    ASSERT_EQ(physicality_descriptor_readback_build(nodes.data(), nodes.size(),
        children.data(), children.size(), &descriptor, 1,
        physicality_descriptor_vocabulary_basis(vocabulary.get()), &limits, kBudget, &raw),
        PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_readback_t, decltype(&physicality_descriptor_readback_free)>
        decoded(raw, physicality_descriptor_readback_free);
    size_t count = 0;
    const auto* got = physicality_descriptor_readback_inputs(decoded.get(), &count);
    ASSERT_EQ(count, 1u);
    const auto expected = original_body.input();
    EXPECT_TRUE(hash128_equals(&got->entity_id, &expected.entity_id));
    EXPECT_EQ(got->type, expected.type);
    EXPECT_EQ(std::memcmp(got->coord, expected.coord, sizeof(expected.coord)), 0);
    EXPECT_EQ(std::memcmp(&got->hilbert_index, &expected.hilbert_index, sizeof(expected.hilbert_index)), 0);
    ASSERT_EQ(got->trajectory_vertices, expected.trajectory_vertices);
    EXPECT_EQ(std::memcmp(got->trajectory_xyzm, expected.trajectory_xyzm,
        expected.trajectory_vertices * 4u * sizeof(double)), 0);
    EXPECT_EQ(got->n_constituents, expected.n_constituents);
    EXPECT_EQ(got->alignment_residual_is_null, 0);
    EXPECT_TRUE(std::signbit(got->alignment_residual));
    EXPECT_EQ(got->source_dim_is_null, 0);
    EXPECT_EQ(got->source_dim, expected.source_dim);
}

TEST_F(PhysicalityDescriptorAdmission, DiagnosedRefusalsRetainExactOwnerAndRetryPublishesCompleteRows) {
    const auto body = composition({atom('A'), atom('B')});
    std::vector<Body> bodies(4096, body);
    auto original = stage(bodies);
    auto captured = capture(original.get());
    ASSERT_NE(captured, nullptr);
    auto sources = witnesses(bodies.size());
    const auto execute = [&](size_t grant,
        physicality_descriptor_materialization_diagnostics_t& diagnostics,
        Materialization& output) {
        physicality_descriptor_materialization_t* raw = nullptr;
        const auto status = physicality_descriptor_materialize_diagnosed_cancelable(
            captured.get(), vocabulary.get(), nullptr, 0, nullptr, 0, nullptr, 0,
            sources.data(), sources.size(), &kSource, 200, grant, nullptr, &diagnostics, &raw);
        output.reset(raw);
        return status;
    };
    Materialization full(nullptr, physicality_descriptor_materialization_free);
    physicality_descriptor_materialization_diagnostics_t full_diagnostics{};
    ASSERT_EQ(execute(kBudget, full_diagnostics, full), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_NE(full, nullptr);
    EXPECT_EQ(full_diagnostics.status, PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(full_diagnostics.phase, PHYSICALITY_MATERIALIZATION_COMPLETE);
    EXPECT_EQ(full_diagnostics.refusal_kind, PHYSICALITY_MATERIALIZATION_REFUSAL_NONE);
    EXPECT_EQ(full_diagnostics.plan.input_count, bodies.size());
    EXPECT_EQ(full_diagnostics.plan.completed_inputs, bodies.size());
    EXPECT_GE(full_diagnostics.released_before_serialization_bytes,
        full_diagnostics.plan.retained_bytes);
    const size_t peak = physicality_descriptor_materialization_peak_bytes(full.get());
    ASSERT_LE(peak, kBudget);

    Materialization refused(nullptr, physicality_descriptor_materialization_free);
    physicality_descriptor_materialization_diagnostics_t refusal{};
    // Too small for the existing fixed basis validation scratch, while large
    // enough to create the result owner. No generated rows may escape.
    ASSERT_EQ(execute(2048u, refusal, refused), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(refused, nullptr);
    EXPECT_EQ(refusal.status, PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(refusal.phase, PHYSICALITY_MATERIALIZATION_ENTRY);
    EXPECT_EQ(refusal.refusal_kind, PHYSICALITY_MATERIALIZATION_REFUSAL_MEMORY_GRANT);
    ASSERT_LE(refusal.retained_bytes, refusal.maximum_bytes);
    EXPECT_GT(refusal.requested_bytes, refusal.maximum_bytes - refusal.retained_bytes);
    EXPECT_LE(refusal.peak_bytes, refusal.maximum_bytes);

    // Derive this refusal from the actual nested planner's grant and mandatory
    // occurrence arrays. It does not guess unique graph counts or hash slack.
    const size_t before_plan = kBudget - full_diagnostics.plan.maximum_bytes;
    const size_t mandatory = bodies.size() *
        (sizeof(hash128_t) + 3u * sizeof(physicality_descriptor_reference_t));
    ASSERT_GT(mandatory, 0u);
    const size_t plan_refusal_grant = before_plan + mandatory - 1u;
    ASSERT_EQ(execute(plan_refusal_grant, refusal, refused), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(refused, nullptr);
    EXPECT_EQ(refusal.phase, PHYSICALITY_MATERIALIZATION_COMBINED_PLAN);
    EXPECT_EQ(refusal.refusal_kind, PHYSICALITY_MATERIALIZATION_REFUSAL_PLAN);
    EXPECT_EQ(refusal.plan.allocation, PHYSICALITY_DESCRIPTOR_PLAN_INITIAL);
    EXPECT_EQ(refusal.plan.refusal, PHYSICALITY_DESCRIPTOR_PLAN_GRANT_REFUSED);
    EXPECT_EQ(refusal.maximum_bytes, refusal.plan.maximum_bytes);
    EXPECT_EQ(refusal.requested_bytes, refusal.plan.requested_bytes);
    EXPECT_GT(refusal.requested_bytes, refusal.maximum_bytes);
    EXPECT_EQ(refusal.plan.input_count, bodies.size());

    Materialization retry(nullptr, physicality_descriptor_materialization_free);
    physicality_descriptor_materialization_diagnostics_t retry_diagnostics{};
    ASSERT_EQ(execute(peak, retry_diagnostics, retry), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_NE(retry, nullptr);
    EXPECT_LE(physicality_descriptor_materialization_peak_bytes(retry.get()), peak);
    EXPECT_EQ(retry_diagnostics.status, PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(retry_diagnostics.refusal_kind, PHYSICALITY_MATERIALIZATION_REFUSAL_NONE);
    size_t full_count = 0, retry_count = 0;
    const auto* full_forms = physicality_descriptor_materialization_forms(full.get(), &full_count);
    const auto* retry_forms = physicality_descriptor_materialization_forms(retry.get(), &retry_count);
    ASSERT_EQ(full_count, bodies.size());
    ASSERT_EQ(retry_count, full_count);
    for (size_t i = 0; i < full_count; ++i) {
        EXPECT_TRUE(hash128_equals(&full_forms[i].descriptor_id, &retry_forms[i].descriptor_id));
        EXPECT_TRUE(hash128_equals(&full_forms[i].view_id, &retry_forms[i].view_id));
        EXPECT_EQ(full_forms[i].view_state, retry_forms[i].view_state);
        EXPECT_EQ(full_forms[i].missing_first, retry_forms[i].missing_first);
        EXPECT_EQ(full_forms[i].missing_count, retry_forms[i].missing_count);
    }
    Stage full_stage(physicality_descriptor_materialization_take_stage(full.get()), intent_stage_free);
    Stage retry_stage(physicality_descriptor_materialization_take_stage(retry.get()), intent_stage_free);
    ASSERT_NE(full_stage, nullptr);
    ASSERT_NE(retry_stage, nullptr);
    for (const auto table : {INTENT_STAGE_TABLE_ENTITIES, INTENT_STAGE_TABLE_PHYSICALITIES,
                            INTENT_STAGE_TABLE_ATTESTATIONS}) {
        size_t full_bytes = 0, retry_bytes = 0;
        const auto* full_data = intent_stage_tuple_ptr(full_stage.get(), table, &full_bytes);
        const auto* retry_data = intent_stage_tuple_ptr(retry_stage.get(), table, &retry_bytes);
        ASSERT_GT(full_bytes, 0u);
        ASSERT_EQ(retry_bytes, full_bytes);
        EXPECT_EQ(std::memcmp(full_data, retry_data, full_bytes), 0);
    }
    // Both refusals left the caller's borrowed source intact.
    EXPECT_EQ(intent_stage_physicality_count(original.get()), bodies.size());
}


struct ObservationPhaseProbe {
    physicality_descriptor_materialization_diagnostics_t* diagnostics = nullptr;
    size_t observations_checkpoints = 0;
    size_t stop = std::numeric_limits<size_t>::max();
    std::chrono::steady_clock::time_point entered{}, serialized{};
    bool finished = false;
    static int requested(void* opaque) {
        auto& self = *static_cast<ObservationPhaseProbe*>(opaque);
        if (self.diagnostics->phase == PHYSICALITY_MATERIALIZATION_OBSERVATIONS) {
            if (self.observations_checkpoints++ == 0) self.entered = std::chrono::steady_clock::now();
            return self.observations_checkpoints >= self.stop;
        }
        if (self.diagnostics->phase == PHYSICALITY_MATERIALIZATION_SERIALIZATION &&
            self.observations_checkpoints != 0 && !self.finished) {
            self.serialized = std::chrono::steady_clock::now();
            self.finished = true;
        }
        return 0;
    }
};

TEST_F(PhysicalityDescriptorAdmission, SourceContextRunsMatchScalarOwnersAcrossBothIdentifiersAndTimes) {
    const auto body = composition({atom('a'), atom('b')});
    const hash128_t other_source{kSource.lo + 1u, kSource.hi};
    const hash128_t other_unit{kUnit.lo, kUnit.hi + 1u};
    const std::array<physicality_descriptor_source_observation_t, 4> pairs{{
        {kSource, kUnit, 0.8}, {kSource, other_unit, 0.8},
        {other_source, other_unit, 0.6}, {other_source, kUnit, 0.6}}};
    const std::array<size_t, 12> order{{0, 0, 1, 1, 2, 2, 3, 3, 0, 1, 2, 3}};
    const std::vector<int64_t> times{3, 9, -5, 2, 0, 17,
        INTENT_STAGE_PG_EPOCH_UNIX_US + 33, INTENT_STAGE_PG_EPOCH_UNIX_US + 20,
        5, -1, 11, INTENT_STAGE_PG_EPOCH_UNIX_US + 5};
    std::vector<physicality_descriptor_source_observation_t> sources;
    std::array<int64_t, 4> latest;
    latest.fill(std::numeric_limits<int64_t>::min());
    for (size_t i = 0; i < order.size(); ++i) {
        sources.push_back(pairs[order[i]]);
        latest[order[i]] = std::max(latest[order[i]], times[i]);
    }
    auto original = stage(std::vector<Body>(order.size(), body), times);
    auto captured = capture(original.get());
    Materialization batch(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, {}, sources, batch), PHYSICALITY_DESCRIPTOR_OK);
    size_t count = 0;
    const auto* forms = physicality_descriptor_materialization_forms(batch.get(), &count);
    ASSERT_EQ(count, order.size());
    for (size_t i = 1; i < count; ++i) {
        EXPECT_TRUE(hash128_equals(&forms[0].descriptor_id, &forms[i].descriptor_id));
        EXPECT_TRUE(hash128_equals(&forms[0].view_id, &forms[i].view_id));
    }
    Stage generated(physicality_descriptor_materialization_take_stage(batch.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    ASSERT_EQ(intent_stage_attestation_count(generated.get()), pairs.size());

    // Each independent one-observation call must compose its source context.
    // Union its exact ordinary tuple rows in first-seen order, retaining each
    // source/unit's latest real timestamp. No second identity recipe is used.
    std::array<std::vector<std::vector<uint8_t>>, 3> expected_rows;
    std::array<std::vector<hash128_t>, 3> expected_ids;
    for (size_t pair = 0; pair < pairs.size(); ++pair) {
        auto scalar_source = stage({body}, {latest[pair]});
        auto scalar_capture = capture(scalar_source.get());
        Materialization scalar(nullptr, physicality_descriptor_materialization_free);
        ASSERT_EQ(run(scalar_capture, {}, {}, {}, {pairs[pair]}, scalar), PHYSICALITY_DESCRIPTOR_OK);
        const auto scalar_form = form(scalar);
        EXPECT_TRUE(hash128_equals(&forms[0].descriptor_id, &scalar_form.descriptor_id));
        Stage scalar_stage(physicality_descriptor_materialization_take_stage(scalar.get()), intent_stage_free);
        ASSERT_NE(scalar_stage, nullptr);
        for (int table = 1; table <= 3; ++table) {
            const size_t slot = static_cast<size_t>(table - 1);
            size_t bytes = 0, at = 0;
            const auto* data = intent_stage_tuple_ptr(scalar_stage.get(),
                static_cast<intent_stage_table_t>(table), &bytes);
            std::vector<Field> fields;
            while (at < bytes) {
                const size_t first = at;
                ASSERT_TRUE(next_row(data, bytes, at, fields));
                ASSERT_FALSE(fields.empty());
                ASSERT_EQ(fields[0].size, sizeof(hash128_t));
                hash128_t id;
                std::memcpy(&id, fields[0].bytes, sizeof(id));
                std::vector<uint8_t> row(data + first, data + at);
                auto found = std::find_if(expected_ids[slot].begin(), expected_ids[slot].end(),
                    [&](const hash128_t& prior) { return hash128_equals(&id, &prior); });
                if (found == expected_ids[slot].end()) {
                    expected_ids[slot].push_back(id);
                    expected_rows[slot].push_back(std::move(row));
                } else {
                    EXPECT_EQ(row, expected_rows[slot][static_cast<size_t>(found - expected_ids[slot].begin())]);
                }
            }
        }
    }
    for (int table = 1; table <= 3; ++table) {
        std::vector<uint8_t> expected;
        for (const auto& row : expected_rows[static_cast<size_t>(table - 1)])
            expected.insert(expected.end(), row.begin(), row.end());
        size_t bytes = 0;
        const auto* data = intent_stage_tuple_ptr(generated.get(), static_cast<intent_stage_table_t>(table), &bytes);
        ASSERT_EQ(bytes, expected.size());
        ASSERT_GT(bytes, 0u);
        EXPECT_EQ(std::memcmp(data, expected.data(), bytes), 0);
    }
}

TEST_F(PhysicalityDescriptorAdmission, CachedObservationContextsKeepCancellationAndPriorConflictChecks) {
    const auto body = composition({atom('a'), atom('b')});
    auto original = stage(std::vector<Body>(128, body));
    auto captured = capture(original.get());
    auto sources = witnesses(128);
    Materialization complete(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, {}, sources, complete), PHYSICALITY_DESCRIPTOR_OK);
    for (size_t stop : {size_t{1}, size_t{39}, size_t{100}}) {
        physicality_descriptor_materialization_diagnostics_t diagnostics{};
        ObservationPhaseProbe probe{&diagnostics, 0, stop};
        physicality_descriptor_cancel_t cancellation{ObservationPhaseProbe::requested, &probe};
        physicality_descriptor_materialization_t* raw = nullptr;
        EXPECT_EQ(physicality_descriptor_materialize_diagnosed_cancelable(captured.get(), vocabulary.get(),
            nullptr, 0, nullptr, 0, nullptr, 0, sources.data(), sources.size(),
            &kSource, 200, kBudget, &cancellation, &diagnostics, &raw), PHYSICALITY_DESCRIPTOR_CANCELLED);
        EXPECT_EQ(raw, nullptr);
        EXPECT_EQ(diagnostics.phase, PHYSICALITY_MATERIALIZATION_OBSERVATIONS);
        EXPECT_EQ(probe.observations_checkpoints, stop);
    }
    auto conflicting = sources;
    conflicting[64].source_trust = 0.2;
    Materialization refused(nullptr, physicality_descriptor_materialization_free);
    EXPECT_EQ(run(captured, {}, {}, {}, conflicting, refused), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(refused, nullptr);
    Materialization retry(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, {}, sources, retry), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(intent_stage_physicality_count(original.get()), sources.size());
    Stage expected(physicality_descriptor_materialization_take_stage(complete.get()), intent_stage_free);
    Stage actual(physicality_descriptor_materialization_take_stage(retry.get()), intent_stage_free);
    for (int table = 1; table <= 3; ++table) {
        size_t want_bytes = 0, got_bytes = 0;
        const auto* want = intent_stage_tuple_ptr(expected.get(), static_cast<intent_stage_table_t>(table), &want_bytes);
        const auto* got = intent_stage_tuple_ptr(actual.get(), static_cast<intent_stage_table_t>(table), &got_bytes);
        ASSERT_EQ(got_bytes, want_bytes);
        ASSERT_GT(got_bytes, 0u);
        EXPECT_EQ(std::memcmp(got, want, got_bytes), 0);
    }
}

TEST_F(PhysicalityDescriptorAdmission, SourceContextObservationWorkload) {
    // Finite genuine native materialization fixture, also compiled unchanged
    // against the pre-optimization library by the hosted comparison. Timings
    // are observations, never a speed threshold or recorded-game benchmark.
    // The instrumented interval starts at the first observation checkpoint and
    // ends at the first serialization checkpoint: it includes owner retirement
    // and serializer entry setup. Whole-call timing below has no callback.
    constexpr size_t rows = 16384;
    constexpr size_t samples = 5;
    const auto body = composition({atom('a'), atom('b')});
    auto original = stage(std::vector<Body>(rows, body));
    auto captured = capture(original.get());
    ASSERT_NE(captured, nullptr);
    const std::array<const char*, 3> labels{{"contiguous", "blocks64", "alternating"}};
    RecordProperty("observation_rows", std::to_string(rows));
    RecordProperty("finite_grant_bytes", std::to_string(kBudget));
    const auto fingerprint = [](const uint8_t* data, size_t bytes) {
        hash128_t digest{};
        hash128_blake3(data, bytes, &digest);
        const auto* octets = reinterpret_cast<const uint8_t*>(&digest);
        const char hex[] = "0123456789abcdef";
        std::string value;
        for (size_t i = 0; i < sizeof(digest); ++i) {
            value.push_back(hex[octets[i] >> 4u]);
            value.push_back(hex[octets[i] & 15u]);
        }
        return value;
    };
    for (size_t pattern = 0; pattern < labels.size(); ++pattern) {
        auto sources = witnesses(rows);
        for (size_t i = 0; i < rows; ++i) {
            const size_t pair = pattern == 0 ? 0 : (pattern == 1 ? (i / 64u) % 4u : i % 4u);
            sources[i].source_id.lo += pair / 2u;
            sources[i].source_unit_id.hi += pair % 2u;
            sources[i].source_trust = pair / 2u == 0 ? 0.8 : 0.6;
        }
        std::array<std::string, 3> expected_hash;
        std::array<size_t, 3> expected_bytes{};
        size_t expected_peak = 0, expected_retained = 0;
        for (size_t sample = 0; sample <= samples; ++sample) {
            physicality_descriptor_materialization_diagnostics_t diagnostics{};
            ObservationPhaseProbe probe{&diagnostics};
            physicality_descriptor_cancel_t cancellation{ObservationPhaseProbe::requested, &probe};
            physicality_descriptor_materialization_t* raw = nullptr;
            ASSERT_EQ(physicality_descriptor_materialize_diagnosed_cancelable(captured.get(), vocabulary.get(),
                nullptr, 0, nullptr, 0, nullptr, 0, sources.data(), sources.size(),
                &kSource, 200, kBudget, &cancellation, &diagnostics, &raw), PHYSICALITY_DESCRIPTOR_OK);
            Materialization phased(raw, physicality_descriptor_materialization_free);
            ASSERT_TRUE(probe.finished);
            ASSERT_GT(probe.observations_checkpoints, rows);
            const auto phase_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
                probe.serialized - probe.entered).count();
            ASSERT_GT(phase_ns, 0);
            const size_t peak = physicality_descriptor_materialization_peak_bytes(phased.get());
            const size_t retained = physicality_descriptor_materialization_bytes(phased.get());
            ASSERT_LE(peak, kBudget);
            if (sample == 0) { expected_peak = peak; expected_retained = retained; }
            EXPECT_EQ(peak, expected_peak);
            EXPECT_EQ(retained, expected_retained);
            Stage phased_stage(physicality_descriptor_materialization_take_stage(phased.get()), intent_stage_free);
            ASSERT_NE(phased_stage, nullptr);
            // Whole-call timing is a separate uninstrumented materialization,
            // excluding capture, fixture construction and output hashing.
            const auto start = std::chrono::steady_clock::now();
            Materialization plain(nullptr, physicality_descriptor_materialization_free);
            ASSERT_EQ(run(captured, {}, {}, {}, sources, plain), PHYSICALITY_DESCRIPTOR_OK);
            const auto whole_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - start).count();
            EXPECT_EQ(physicality_descriptor_materialization_peak_bytes(plain.get()), expected_peak);
            Stage plain_stage(physicality_descriptor_materialization_take_stage(plain.get()), intent_stage_free);
            ASSERT_NE(plain_stage, nullptr);
            size_t form_count = 0;
            const auto* forms = physicality_descriptor_materialization_forms(plain.get(), &form_count);
            ASSERT_NE(forms, nullptr);
            ASSERT_EQ(form_count, rows);
            for (int table = 1; table <= 3; ++table) {
                const size_t slot = static_cast<size_t>(table - 1);
                size_t bytes = 0, plain_bytes = 0;
                const auto* data = intent_stage_tuple_ptr(phased_stage.get(),
                    static_cast<intent_stage_table_t>(table), &bytes);
                const auto* plain_data = intent_stage_tuple_ptr(plain_stage.get(),
                    static_cast<intent_stage_table_t>(table), &plain_bytes);
                ASSERT_GT(bytes, 0u);
                ASSERT_EQ(plain_bytes, bytes);
                EXPECT_EQ(std::memcmp(plain_data, data, bytes), 0);
                const auto hash = fingerprint(data, bytes);
                if (sample == 0) {
                    expected_hash[slot] = hash;
                    expected_bytes[slot] = bytes;
                    RecordProperty(std::string(labels[pattern]) + "_table" + std::to_string(table) + "_hash128", hash);
                    RecordProperty(std::string(labels[pattern]) + "_table" + std::to_string(table) + "_bytes", std::to_string(bytes));
                }
                EXPECT_EQ(hash, expected_hash[slot]);
                EXPECT_EQ(bytes, expected_bytes[slot]);
            }
            if (sample != 0) {
                const std::string key = std::string(labels[pattern]) + "_sample" + std::to_string(sample);
                RecordProperty(key + "_observations_to_serialization_checkpoint_ns", std::to_string(phase_ns));
                RecordProperty(key + "_uninstrumented_materialization_ns", std::to_string(whole_ns));
            }
        }
        RecordProperty(std::string(labels[pattern]) + "_peak_bytes", std::to_string(expected_peak));
        RecordProperty(std::string(labels[pattern]) + "_retained_bytes", std::to_string(expected_retained));
    }
}

} // namespace
