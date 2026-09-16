#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/trajectory.h"

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
    EXPECT_EQ(run(raw_capture, {}, {}, missing, witnesses(3), result), PHYSICALITY_DESCRIPTOR_MISSING_REFERENCE);
    EXPECT_EQ(result, nullptr);
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
    EXPECT_EQ(run(captured, {}, {}, {child.value.entity_id}, witnesses(1), pending),
        PHYSICALITY_DESCRIPTOR_MISSING_REFERENCE);
    EXPECT_EQ(pending, nullptr);
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
        ASSERT_EQ(rows[i].type, 1);
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

} // namespace
