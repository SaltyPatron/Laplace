#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <memory>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/physicality_descriptor_admission.h"
#include "laplace/core/trajectory.h"
#include "../src/physicality_descriptor_provider.h"

namespace {
constexpr size_t kBudget = 64u * 1024u * 1024u;
const hash128_t kSource{0x123, 0x456}, kUnit{0x789, 0xabc};
using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
using Capture = std::unique_ptr<physicality_descriptor_capture_t, decltype(&physicality_descriptor_capture_free)>;
using Result = std::unique_ptr<physicality_descriptor_materialization_t, decltype(&physicality_descriptor_materialization_free)>;
using Vocabulary = std::unique_ptr<physicality_descriptor_vocabulary_t, decltype(&physicality_descriptor_vocabulary_free)>;

struct Body {
    physicality_descriptor_input_t value{};
    std::vector<double> trajectory;
    physicality_descriptor_input_t input() const {
        auto out = value;
        out.trajectory_xyzm = trajectory.empty() ? nullptr : trajectory.data();
        return out;
    }
};

Body atom(uint32_t cp) {
    Body out;
    out.value.type = 1;
    out.value.alignment_residual_is_null = out.value.source_dim_is_null = 1;
    EXPECT_EQ(codepoint_table_resolve_atom(cp, &out.value.entity_id,
        out.value.coord, &out.value.hilbert_index), 0);
    return out;
}

Body compose(const std::vector<Body>& children) {
    Body out;
    out.value.type = 1;
    out.value.alignment_residual_is_null = out.value.source_dim_is_null = 1;
    std::vector<hash128_t> ids;
    std::vector<double> coords;
    for (const auto& child : children) {
        ids.push_back(child.value.entity_id);
        coords.insert(coords.end(), child.value.coord, child.value.coord + 4);
    }
    hash_composer_compose_node(4, ids.data(), coords.data(), ids.size(),
        &out.value.entity_id, out.value.coord, &out.value.hilbert_index);
    out.trajectory.resize(children.size() * 4u);
    EXPECT_EQ(trajectory_build(ids.data(), ids.size(), out.trajectory.data()), 0);
    out.value.trajectory_vertices = children.size();
    out.value.n_constituents = static_cast<int32_t>(children.size());
    return out;
}

Stage stage(const std::vector<Body>& bodies, std::vector<int64_t> times = {}) {
    Stage out(intent_stage_new_bounded(0, kBudget), intent_stage_free);
    EXPECT_NE(out, nullptr);
    std::vector<physicality_descriptor_input_t> inputs;
    for (const auto& body : bodies) inputs.push_back(body.input());
    if (times.empty()) times.assign(bodies.size(), 123);
    EXPECT_EQ(physicality_descriptor_stage_add_batch(out.get(), inputs.data(), nullptr,
        times.data(), inputs.size()), PHYSICALITY_DESCRIPTOR_OK);
    return out;
}

bool equal(const hash128_t& a, const hash128_t& b) { return hash128_equals(&a, &b) != 0; }

std::vector<hash128_t> constituents(const physicality_descriptor_input_t& body) {
    std::vector<hash128_t> out(static_cast<size_t>(body.n_constituents));
    EXPECT_EQ(trajectory_constituents(body.trajectory_xyzm, body.trajectory_vertices,
        out.data(), out.size()), body.n_constituents);
    return out;
}

const physicality_descriptor_input_t* find_body(const Capture& rows, const hash128_t& id) {
    size_t count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(rows.get(), &count);
    for (size_t i = 0; i < count; ++i) if (equal(bodies[i].entity_id, id)) return &bodies[i];
    return nullptr;
}

class PhysicalityDescriptorRetention : public ::testing::Test {
protected:
    Vocabulary vocabulary{nullptr, physicality_descriptor_vocabulary_free};
    void SetUp() override {
        ASSERT_TRUE(codepoint_table_is_loaded());
        physicality_descriptor_vocabulary_t* raw = nullptr;
        ASSERT_EQ(physicality_descriptor_vocabulary_create(&kSource, kBudget, &raw), PHYSICALITY_DESCRIPTOR_OK);
        vocabulary.reset(raw);
    }
    Capture capture(const intent_stage_t* source) {
        physicality_descriptor_capture_t* raw = nullptr;
        const physicality_descriptor_limits_t limits{kBudget};
        EXPECT_EQ(physicality_descriptor_capture_stages(&source, 1, &vocabulary->basis,
            &limits, kBudget, &raw), PHYSICALITY_DESCRIPTOR_OK);
        return Capture(raw, physicality_descriptor_capture_free);
    }
    physicality_descriptor_status_t run(const Capture& source,
        const std::vector<const intent_stage_t*>& current,
        const std::vector<hash128_t>& missing, Result& out,
        std::vector<physicality_descriptor_source_observation_t> witnesses = {}) {
        size_t count = 0;
        physicality_descriptor_capture_inputs(source.get(), &count);
        if (witnesses.empty()) witnesses.assign(count, {kSource, kUnit, 0.8});
        physicality_descriptor_materialization_t* raw = nullptr;
        const auto status = physicality_descriptor_materialize(source.get(), vocabulary.get(),
            current.data(), current.size(), nullptr, 0, missing.data(), missing.size(),
            witnesses.data(), witnesses.size(), &kSource, 456, kBudget, &raw);
        out.reset(raw);
        return status;
    }
    physicality_descriptor_admitted_form_t form(const Result& result, size_t at = 0) {
        size_t count = 0;
        const auto* forms = physicality_descriptor_materialization_forms(result.get(), &count);
        EXPECT_LT(at, count);
        return at < count ? forms[at] : physicality_descriptor_admitted_form_t{};
    }
    void expect_missing(const Result& result, size_t at, std::vector<hash128_t> expected) {
        const auto entry = form(result, at);
        ASSERT_EQ(entry.view_state, PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE);
        size_t count = 0;
        const auto* missing = physicality_descriptor_materialization_missing(result.get(), &count);
        ASSERT_LE(entry.missing_first, count);
        ASSERT_LE(entry.missing_count, count - entry.missing_first);
        std::sort(expected.begin(), expected.end(), [](const auto& a, const auto& b) {
            return hash128_compare(&a, &b) < 0;
        });
        ASSERT_EQ(entry.missing_count, expected.size());
        for (size_t i = 0; i < expected.size(); ++i)
            EXPECT_TRUE(equal(missing[entry.missing_first + i], expected[i]));
    }
};

TEST_F(PhysicalityDescriptorRetention, CheckedAbsenceKeepsPerFormSortedTransitiveFrontiers) {
    const auto x = compose({atom('a'), atom('b')});
    const auto y = compose({atom('c'), atom('d')});
    const auto z = compose({atom('e'), atom('f')});
    const auto bridge = compose({y, x, y});
    const auto root = compose({bridge, z, x});
    const auto local = compose({z, atom('g')});
    const auto complete = compose({atom('h'), atom('i')});
    auto originals = stage({root, local, complete, root});
    auto providers = stage({bridge});
    auto captured = capture(originals.get());
    ASSERT_NE(captured, nullptr);
    Result result(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {providers.get()}, {z.value.entity_id, x.value.entity_id,
        y.value.entity_id, x.value.entity_id, hash128_t{91, 92}}, result), PHYSICALITY_DESCRIPTOR_OK);
    expect_missing(result, 0, {x.value.entity_id, y.value.entity_id, z.value.entity_id});
    expect_missing(result, 1, {z.value.entity_id});
    expect_missing(result, 3, {x.value.entity_id, y.value.entity_id, z.value.entity_id});
    EXPECT_EQ(form(result, 2).view_state, PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE);
    EXPECT_EQ(form(result, 2).missing_count, 0u);
    EXPECT_EQ(form(result, 0).missing_first, form(result, 3).missing_first);
    size_t count = 0;
    const auto* original_descriptors = physicality_descriptor_plan_roots(
        physicality_descriptor_capture_plan(captured.get()), &count);
    ASSERT_EQ(count, 4u);
    for (size_t i = 0; i < count; ++i) EXPECT_TRUE(equal(form(result, i).descriptor_id, original_descriptors[i]));
    size_t missing_count = 0;
    physicality_descriptor_materialization_missing(result.get(), &missing_count);
    EXPECT_EQ(missing_count, 4u); // No provider-only or unrelated absence range.
    Stage generated(physicality_descriptor_materialization_take_stage(result.get()), intent_stage_free);
    ASSERT_NE(generated, nullptr);
    auto retained = capture(generated.get());
    ASSERT_NE(retained, nullptr);
    for (size_t i = 0; i < count; ++i) {
        const auto* body = find_body(retained, original_descriptors[i]);
        ASSERT_NE(body, nullptr);
        EXPECT_EQ(body->type, PHYSICALITY_DESCRIPTOR_RETENTION_TYPE);
    }
    for (const auto& absent : {x, y, z}) EXPECT_EQ(find_body(retained, absent.value.entity_id), nullptr);
}

TEST_F(PhysicalityDescriptorRetention, UnqueriedReferencePublishesOnlyPendingFrontier) {
    const auto child = compose({atom('j'), atom('k')});
    const auto parent = compose({child, child});
    auto source = stage({parent});
    auto captured = capture(source.get());
    Result pending(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {}, pending), PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    size_t count = 0;
    const auto* ids = physicality_descriptor_materialization_pending(pending.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_TRUE(equal(ids[0], child.value.entity_id));
    physicality_descriptor_materialization_forms(pending.get(), &count);
    EXPECT_EQ(count, 0u);
    physicality_descriptor_materialization_missing(pending.get(), &count);
    EXPECT_EQ(count, 0u);
    EXPECT_EQ(physicality_descriptor_materialization_take_stage(pending.get()), nullptr);
    ASSERT_EQ(run(captured, {}, {child.value.entity_id}, pending), PHYSICALITY_DESCRIPTOR_OK);
    expect_missing(pending, 0, {child.value.entity_id});
    Stage retained(physicality_descriptor_materialization_take_stage(pending.get()), intent_stage_free);
    EXPECT_NE(retained, nullptr);
}

TEST_F(PhysicalityDescriptorRetention, OpaqueReferencesUseRealTypedLiteralGeometryWithoutReplacingChildren) {
    auto original = compose({atom('l'), atom('m')});
    original.value.type = 3;
    original.value.entity_id = {0xdeadbeef, 0xfedcba9876543210};
    const hash128_t unknown{0x1020304050607080, 0x9988776655443322};
    const hash128_t refs[] = {unknown, atom('m').value.entity_id};
    ASSERT_EQ(trajectory_build(refs, 2, original.trajectory.data()), 0);
    auto raw = stage({original});
    auto captured = capture(raw.get());
    Result result(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {unknown}, result), PHYSICALITY_DESCRIPTOR_OK);
    expect_missing(result, 0, {unknown});
    const auto descriptor = form(result).descriptor_id;
    Stage generated(physicality_descriptor_materialization_take_stage(result.get()), intent_stage_free);
    auto retained = capture(generated.get());
    ASSERT_NE(retained, nullptr);
    EXPECT_EQ(find_body(retained, original.value.entity_id), nullptr);
    EXPECT_EQ(find_body(retained, unknown), nullptr);

    std::array<hash128_t,17> literal_children{};
    std::array<double,68> literal_coords{};
    literal_children[0] = vocabulary->retention_reference_schema.id;
    std::copy_n(vocabulary->retention_reference_schema.coord, 4, literal_coords.begin());
    const auto* octets = reinterpret_cast<const uint8_t*>(&original.value.entity_id);
    for (size_t i = 0; i < 16; ++i) {
        literal_children[i + 1] = vocabulary->basis.byte_numbers[octets[i]];
        std::copy_n(vocabulary->numbers[octets[i]].coord, 4, literal_coords.begin() + (i + 1) * 4);
    }
    hash128_t literal_id{};
    double literal_coord[4]{};
    hilbert128_t literal_hilbert{};
    hash_composer_compose_node(4, literal_children.data(), literal_coords.data(), literal_children.size(),
        &literal_id, literal_coord, &literal_hilbert);
    const auto* literal = find_body(retained, literal_id);
    ASSERT_NE(literal, nullptr);
    EXPECT_EQ(literal->type, 1);
    const auto stored_literal = constituents(*literal);
    ASSERT_EQ(stored_literal.size(), literal_children.size());
    EXPECT_EQ(std::memcmp(stored_literal.data(), literal_children.data(), sizeof(literal_children)), 0);
    EXPECT_EQ(std::memcmp(literal->coord, literal_coord, sizeof(literal_coord)), 0);
    EXPECT_EQ(std::memcmp(&literal->hilbert_index, &literal_hilbert, sizeof(literal_hilbert)), 0);

    const auto* root = find_body(retained, descriptor);
    ASSERT_NE(root, nullptr);
    const auto children = constituents(*root);
    ASSERT_EQ(children.size(), 9u);
    EXPECT_TRUE(equal(children[1], original.value.entity_id));
    EXPECT_FALSE(equal(children[1], literal_id));
    std::array<double,36> coordinates{};
    for (size_t i = 0; i < children.size(); ++i) {
        const double* coord = nullptr;
        if (i == 0) coord = vocabulary->tags[PHYSICALITY_DESCRIPTOR_SCHEMA].coord;
        else if (i == 1) coord = literal_coord;
        else {
            const auto* child = find_body(retained, children[i]);
            if (child != nullptr) coord = child->coord;
            else for (const auto& tag : vocabulary->tags) {
                if (equal(tag.id, children[i])) coord = tag.coord;
            }
            ASSERT_NE(coord, nullptr);
        }
        std::copy_n(coord, 4, coordinates.begin() + i * 4);
    }
    hash128_t expected_id{};
    double expected_coord[4]{};
    hilbert128_t expected_hilbert{};
    hash_composer_compose_node(4, children.data(), coordinates.data(), children.size(),
        &expected_id, expected_coord, &expected_hilbert);
    EXPECT_TRUE(equal(expected_id, descriptor));
    EXPECT_EQ(std::memcmp(root->coord, expected_coord, sizeof(expected_coord)), 0);
    EXPECT_EQ(std::memcmp(&root->hilbert_index, &expected_hilbert, sizeof(expected_hilbert)), 0);

    const auto* trajectory = find_body(retained, children[5]);
    ASSERT_NE(trajectory, nullptr);
    const auto trajectory_children = constituents(*trajectory);
    ASSERT_EQ(trajectory_children.size(), 3u);
    const auto* carrier = find_body(retained, trajectory_children[1]);
    ASSERT_NE(carrier, nullptr);
    const auto carrier_children = constituents(*carrier);
    ASSERT_EQ(carrier_children.size(), 5u);
    EXPECT_TRUE(equal(carrier_children[1], unknown));
    octets = reinterpret_cast<const uint8_t*>(&unknown);
    for (size_t i = 0; i < 16; ++i) {
        literal_children[i + 1] = vocabulary->basis.byte_numbers[octets[i]];
        std::copy_n(vocabulary->numbers[octets[i]].coord, 4, literal_coords.begin() + (i + 1) * 4);
    }
    hash_composer_compose_node(4, literal_children.data(), literal_coords.data(), literal_children.size(),
        &literal_id, literal_coord, &literal_hilbert);
    const auto* carrier_literal = find_body(retained, literal_id);
    ASSERT_NE(carrier_literal, nullptr);
    EXPECT_EQ(carrier_literal->type, 1);
    EXPECT_EQ(std::memcmp(carrier_literal->coord, literal_coord, sizeof(literal_coord)), 0);
    std::copy_n(vocabulary->tags[PHYSICALITY_DESCRIPTOR_CARRIER].coord, 4, coordinates.begin());
    std::copy_n(literal_coord, 4, coordinates.begin() + 4);
    for (size_t i = 2; i < carrier_children.size(); ++i) {
        const auto* child = find_body(retained, carrier_children[i]);
        ASSERT_NE(child, nullptr);
        std::copy_n(child->coord, 4, coordinates.begin() + i * 4);
    }
    hash_composer_compose_node(4, carrier_children.data(), coordinates.data(), carrier_children.size(),
        &expected_id, expected_coord, &expected_hilbert);
    EXPECT_TRUE(equal(expected_id, carrier->entity_id));
    EXPECT_EQ(std::memcmp(carrier->coord, expected_coord, sizeof(expected_coord)), 0);
    EXPECT_EQ(std::memcmp(&carrier->hilbert_index, &expected_hilbert, sizeof(expected_hilbert)), 0);
}

TEST_F(PhysicalityDescriptorRetention, ProviderChangesOnlyViewAndRetainedDescriptorReadsExactSourceBody) {
    const auto child = compose({atom('n'), atom('o')});
    auto alternate = child;
    alternate.value.coord[0] += 0.125;
    hilbert4d_encode(alternate.value.coord, &alternate.value.hilbert_index);
    auto original = compose({child, atom('p')});
    original.value.type = 3;
    original.value.coord[0] = -0.0;
    original.value.alignment_residual_is_null = 0;
    original.value.alignment_residual = -0.0;
    original.value.source_dim_is_null = 0;
    original.value.source_dim = 17;
    auto raw = stage({original}), first_provider = stage({child}), second_provider = stage({alternate});
    auto captured = capture(raw.get());
    Result absent(nullptr, physicality_descriptor_materialization_free), first(nullptr, physicality_descriptor_materialization_free), second(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {child.value.entity_id}, absent), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(captured, {first_provider.get()}, {}, first), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(captured, {second_provider.get()}, {}, second), PHYSICALITY_DESCRIPTOR_OK);
    const auto descriptor = form(absent).descriptor_id;
    EXPECT_TRUE(equal(descriptor, form(first).descriptor_id));
    EXPECT_TRUE(equal(descriptor, form(second).descriptor_id));
    EXPECT_EQ(form(first).view_state, PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE);
    EXPECT_EQ(form(second).view_state, PHYSICALITY_DESCRIPTOR_VIEW_AVAILABLE);
    EXPECT_FALSE(equal(form(first).view_id, form(second).view_id));
    Stage absent_stage(physicality_descriptor_materialization_take_stage(absent.get()), intent_stage_free);
    Stage first_stage(physicality_descriptor_materialization_take_stage(first.get()), intent_stage_free);
    Stage second_stage(physicality_descriptor_materialization_take_stage(second.get()), intent_stage_free);
    auto absent_rows = capture(absent_stage.get()), first_rows = capture(first_stage.get()), second_rows = capture(second_stage.get());
    const auto* root = find_body(absent_rows, descriptor);
    ASSERT_NE(root, nullptr);
    for (const auto* rows : {&first_rows, &second_rows}) {
        const auto* candidate = find_body(*rows, descriptor);
        ASSERT_NE(candidate, nullptr);
        EXPECT_EQ(candidate->type, PHYSICALITY_DESCRIPTOR_RETENTION_TYPE);
        EXPECT_EQ(std::memcmp(candidate->coord, root->coord, sizeof(root->coord)), 0);
        EXPECT_EQ(std::memcmp(&candidate->hilbert_index, &root->hilbert_index, sizeof(root->hilbert_index)), 0);
        ASSERT_EQ(candidate->trajectory_vertices, root->trajectory_vertices);
        EXPECT_EQ(std::memcmp(candidate->trajectory_xyzm, root->trajectory_xyzm, root->trajectory_vertices * 4u * sizeof(double)), 0);
    }
    size_t count = 0;
    const auto* bodies = physicality_descriptor_capture_inputs(absent_rows.get(), &count);
    std::vector<physicality_descriptor_node_t> nodes;
    std::vector<hash128_t> children;
    for (size_t i = 0; i < count; ++i) {
        const auto ids = constituents(bodies[i]);
        nodes.push_back({bodies[i].entity_id, children.size(), ids.size()});
        children.insert(children.end(), ids.begin(), ids.end());
    }
    physicality_descriptor_readback_t* decoded_raw = nullptr;
    const physicality_descriptor_limits_t limits{kBudget};
    ASSERT_EQ(physicality_descriptor_readback_build(nodes.data(), nodes.size(), children.data(), children.size(),
        &descriptor, 1, &vocabulary->basis, &limits, kBudget, &decoded_raw), PHYSICALITY_DESCRIPTOR_OK);
    std::unique_ptr<physicality_descriptor_readback_t, decltype(&physicality_descriptor_readback_free)>
        decoded(decoded_raw, physicality_descriptor_readback_free);
    const auto* restored = physicality_descriptor_readback_inputs(decoded.get(), &count);
    ASSERT_EQ(count, 1u);
    EXPECT_TRUE(equal(restored->entity_id, original.value.entity_id));
    EXPECT_EQ(restored->type, original.value.type);
    EXPECT_EQ(std::memcmp(restored->coord, original.value.coord, sizeof(original.value.coord)), 0);
    EXPECT_EQ(std::memcmp(&restored->hilbert_index, &original.value.hilbert_index, sizeof(original.value.hilbert_index)), 0);
    ASSERT_EQ(restored->trajectory_vertices, original.value.trajectory_vertices);
    EXPECT_EQ(std::memcmp(restored->trajectory_xyzm, original.trajectory.data(), original.trajectory.size() * sizeof(double)), 0);
    EXPECT_EQ(restored->n_constituents, original.value.n_constituents);
    EXPECT_EQ(restored->alignment_residual_is_null, 0);
    EXPECT_EQ(std::memcmp(&restored->alignment_residual, &original.value.alignment_residual, sizeof(double)), 0);
    EXPECT_EQ(restored->source_dim_is_null, 0);
    EXPECT_EQ(restored->source_dim, 17);
}

TEST_F(PhysicalityDescriptorRetention, TypeNineAuthenticatesCanonicalManifestAndChargesHashWork) {
    auto body = compose({atom('q'), atom('r'), atom('s')});
    body.value.type = PHYSICALITY_DESCRIPTOR_RETENTION_TYPE;
    auto raw = stage({body});
    const intent_stage_t* pointer = raw.get();
    size_t bodies = 0, vertices = 0, logical = 0;
    EXPECT_EQ(physicality_descriptor_stages_preflight(&pointer, 1, 2, &bodies, &vertices, &logical),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    ASSERT_EQ(physicality_descriptor_stages_preflight(&pointer, 1, 3, &bodies, &vertices, &logical), PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(bodies, 1u);
    EXPECT_EQ(vertices, 3u);
    EXPECT_EQ(logical, 3u);
    auto captured = capture(raw.get());
    ASSERT_NE(captured, nullptr);
    auto wrong = body;
    wrong.value.entity_id.lo ^= 1u;
    EXPECT_EQ(laplace_physicality_manifest_validate(&wrong.value.entity_id, wrong.value.type,
        wrong.trajectory.data(), wrong.value.trajectory_vertices, wrong.value.n_constituents), -4);
    physicality_descriptor_plan_t* plan = nullptr;
    const physicality_descriptor_limits_t limits{kBudget};
    auto input = wrong.input();
    EXPECT_EQ(physicality_descriptor_plan_build(&input, 1, &vocabulary->basis, &limits, &plan), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(plan, nullptr);
    auto singleton = compose({atom('t')});
    singleton.value.type = PHYSICALITY_DESCRIPTOR_RETENTION_TYPE;
    EXPECT_EQ(laplace_physicality_manifest_validate(&singleton.value.entity_id, singleton.value.type,
        singleton.trajectory.data(), 1, 1), -3);
    EXPECT_EQ(laplace_physicality_manifest_validate(&singleton.value.entity_id, singleton.value.type,
        nullptr, 0, 0), -3);
}

TEST_F(PhysicalityDescriptorRetention, SourceUnitReplayIsUnchangedWhenViewIsUnavailable) {
    const auto child = compose({atom('u'), atom('v')});
    const auto parent = compose({child, atom('w')});
    auto raw = stage({parent, parent, parent}, {3, 9, 5});
    auto provider = stage({child});
    auto captured = capture(raw.get());
    std::vector<physicality_descriptor_source_observation_t> sources(3, {kSource, kUnit, 0.8});
    sources[2].source_unit_id.lo += 1;
    Result absent(nullptr, physicality_descriptor_materialization_free), available(nullptr, physicality_descriptor_materialization_free);
    ASSERT_EQ(run(captured, {}, {child.value.entity_id}, absent, sources), PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(run(captured, {provider.get()}, {}, available, sources), PHYSICALITY_DESCRIPTOR_OK);
    for (size_t i = 0; i < 3; ++i) {
        expect_missing(absent, i, {child.value.entity_id});
        EXPECT_TRUE(equal(form(absent, i).descriptor_id, form(available, i).descriptor_id));
    }
    Stage absent_stage(physicality_descriptor_materialization_take_stage(absent.get()), intent_stage_free);
    Stage available_stage(physicality_descriptor_materialization_take_stage(available.get()), intent_stage_free);
    ASSERT_EQ(intent_stage_attestation_count(absent_stage.get()), 2u);
    ASSERT_EQ(intent_stage_attestation_count(available_stage.get()), 2u);
    size_t absent_bytes = 0, available_bytes = 0;
    const auto* a = intent_stage_tuple_ptr(absent_stage.get(), INTENT_STAGE_TABLE_ATTESTATIONS, &absent_bytes);
    const auto* b = intent_stage_tuple_ptr(available_stage.get(), INTENT_STAGE_TABLE_ATTESTATIONS, &available_bytes);
    ASSERT_EQ(absent_bytes, available_bytes);
    EXPECT_EQ(std::memcmp(a, b, absent_bytes), 0);
}
} // namespace
