#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

#include "laplace/core/mantissa.h"
#include "laplace/core/physicality_descriptor.h"
#include "laplace/core/trajectory.h"

namespace {

constexpr size_t kBytes = 64u * 1024u * 1024u;
using Plan = std::unique_ptr<physicality_descriptor_plan_t, decltype(&physicality_descriptor_plan_free)>;
using Readback = std::unique_ptr<physicality_descriptor_readback_t, decltype(&physicality_descriptor_readback_free)>;

// Controlled symbolic vocabulary for native representation tests, not an
// installed floor or a database witness. All descriptor IDs use the real owner.
physicality_descriptor_basis_t vocabulary() {
    physicality_descriptor_basis_t result{};
    for (size_t i = 0; i < PHYSICALITY_DESCRIPTOR_TAG_COUNT; ++i)
        result.tags[i] = hash128_t{UINT64_C(0x9876543200000000) + i, 37};
    for (size_t i = 0; i < 256; ++i)
        result.byte_numbers[i] = hash128_t{UINT64_C(0x456789ab00000000) + i, 41};
    return result;
}

bool same(const hash128_t& a, const hash128_t& b) { return hash128_equals(&a, &b); }

struct Catalog {
    physicality_descriptor_basis_t basis = vocabulary();
    std::vector<physicality_descriptor_node_t> nodes;
    std::vector<hash128_t> children, roots;

    explicit Catalog(const std::vector<physicality_descriptor_input_t>& inputs) {
        const physicality_descriptor_limits_t limits{kBytes};
        physicality_descriptor_plan_t* raw = nullptr;
        EXPECT_EQ(physicality_descriptor_plan_build(inputs.data(), inputs.size(), &basis,
            &limits, &raw), PHYSICALITY_DESCRIPTOR_OK);
        Plan plan(raw, physicality_descriptor_plan_free);
        if (!plan) return;
        size_t count = 0;
        const auto* n = physicality_descriptor_plan_nodes(plan.get(), &count);
        nodes.assign(n, n + count);
        const auto* c = physicality_descriptor_plan_children(plan.get(), &count);
        children.assign(c, c + count);
        const auto* r = physicality_descriptor_plan_roots(plan.get(), &count);
        roots.assign(r, r + count);
    }

    const physicality_descriptor_node_t* find(const hash128_t& id) const {
        for (const auto& node : nodes) if (same(node.id, id)) return &node;
        return nullptr;
    }

    void rehash() {
        std::vector<std::pair<hash128_t, hash128_t>> changes;
        for (auto& node : nodes) {
            for (size_t i = 0; i < node.child_count; ++i)
                for (const auto& [old_id, new_id] : changes)
                    if (same(children[node.first_child + i], old_id)) {
                        children[node.first_child + i] = new_id;
                        break;
                    }
            const hash128_t old = node.id;
            hash128_merkle(0, children.data() + node.first_child, node.child_count, &node.id);
            changes.emplace_back(old, node.id);
        }
        for (auto& root : roots)
            for (const auto& [old_id, new_id] : changes)
                if (same(root, old_id)) { root = new_id; break; }
    }
};

struct Hydrated {
    std::vector<physicality_descriptor_node_t> nodes;
    std::vector<hash128_t> children;

    void add(const Catalog& source, const hash128_t& id) {
        const auto* node = source.find(id);
        ASSERT_NE(node, nullptr) << "Frontier attempted to fetch a terminal/external entity";
        auto copy = *node;
        copy.first_child = children.size();
        children.insert(children.end(), source.children.begin() + node->first_child,
            source.children.begin() + node->first_child + node->child_count);
        nodes.push_back(copy);
    }

    physicality_descriptor_status_t prepare(const Catalog& source,
        physicality_descriptor_readback_t** out, size_t bytes = kBytes,
        size_t logical = SIZE_MAX, size_t plan_bytes = kBytes) const {
        const physicality_descriptor_limits_t limits{plan_bytes};
        return physicality_descriptor_readback_prepare(nodes.data(), nodes.size(),
            children.data(), children.size(), source.roots.data(), source.roots.size(),
            &source.basis, &limits, bytes, logical, out);
    }
};

physicality_descriptor_input_t body() {
    physicality_descriptor_input_t result{};
    result.entity_id = hash128_t{0xabc, 0xdef};
    result.type = 3;
    result.coord[0] = -0.0;
    result.coord[1] = std::nextafter(0.5, 1.0);
    result.coord[2] = std::numeric_limits<double>::denorm_min();
    result.coord[3] = -0.25;
    result.hilbert_index.bytes[2] = 0xfe;
    result.alignment_residual_is_null = 1;
    result.source_dim_is_null = 1;
    return result;
}

void expect_body(const physicality_descriptor_input_t& actual,
                 const physicality_descriptor_input_t& expected) {
    EXPECT_TRUE(same(actual.entity_id, expected.entity_id));
    EXPECT_EQ(actual.type, expected.type);
    EXPECT_EQ(std::memcmp(actual.coord, expected.coord, sizeof(actual.coord)), 0);
    EXPECT_EQ(std::memcmp(&actual.hilbert_index, &expected.hilbert_index, sizeof(actual.hilbert_index)), 0);
    ASSERT_EQ(actual.trajectory_vertices, expected.trajectory_vertices);
    if (expected.trajectory_vertices) {
        EXPECT_EQ(std::memcmp(actual.trajectory_xyzm, expected.trajectory_xyzm,
            expected.trajectory_vertices * 4u * sizeof(double)), 0);
    } else { EXPECT_EQ(actual.trajectory_xyzm, nullptr); }
    EXPECT_EQ(actual.n_constituents, expected.n_constituents);
    EXPECT_EQ(actual.alignment_residual_is_null, expected.alignment_residual_is_null);
    if (!expected.alignment_residual_is_null) {
        EXPECT_EQ(std::memcmp(&actual.alignment_residual, &expected.alignment_residual, sizeof(double)), 0);
    }
    EXPECT_EQ(actual.source_dim_is_null, expected.source_dim_is_null);
    if (!expected.source_dim_is_null) { EXPECT_EQ(actual.source_dim, expected.source_dim); }
}

Hydrated all(const Catalog& source) {
    Hydrated result;
    for (const auto& node : source.nodes) result.add(source, node.id);
    return result;
}

TEST(PhysicalityDescriptorFrontier, HydratesWholeTypedLayersWithoutFollowingTerminalEntities) {
    const hash128_t child{0x111, 0x222};
    mantissa_payload_t payload{};
    payload.entity_id = child;
    payload.run_length = 7;
    payload.flags = laplace_vertex_flags(200, 0, 0);
    double trajectory[4]{};
    mantissa_pack(trajectory, &payload);
    auto first = body();
    first.trajectory_xyzm = trajectory;
    first.trajectory_vertices = 1;
    first.n_constituents = 7;
    first.alignment_residual_is_null = 0;
    first.alignment_residual = -0.0;
    first.source_dim_is_null = 0;
    first.source_dim = 4096;
    auto second = first;
    second.coord[3] = 0.75;
    const std::vector<physicality_descriptor_input_t> inputs{first, second, first};
    Catalog source(inputs);
    ASSERT_FALSE(source.nodes.empty());
    Hydrated hydrated;
    std::vector<hash128_t> requested;
    size_t rounds = 0, largest_frontier = 0;
    for (; rounds <= source.nodes.size(); ++rounds) {
        physicality_descriptor_readback_t* raw = nullptr;
        const auto status = hydrated.prepare(source, &raw);
        Readback result(raw, physicality_descriptor_readback_free);
        ASSERT_NE(result, nullptr);
        if (status == PHYSICALITY_DESCRIPTOR_OK) {
            size_t count = 0;
            const auto* decoded = physicality_descriptor_readback_inputs(result.get(), &count);
            ASSERT_EQ(count, inputs.size());
            for (size_t i = 0; i < count; ++i) expect_body(decoded[i], inputs[i]);
            EXPECT_EQ(physicality_descriptor_readback_missing(result.get(), &count), nullptr);
            EXPECT_EQ(count, 0u);
            break;
        }
        ASSERT_EQ(status, PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
        size_t count = 99;
        EXPECT_EQ(physicality_descriptor_readback_inputs(result.get(), &count), nullptr);
        EXPECT_EQ(count, 0u);
        EXPECT_EQ(physicality_descriptor_readback_content_hash_operands(result.get()), 0u);
        const auto* frontier = physicality_descriptor_readback_missing(result.get(), &count);
        ASSERT_NE(frontier, nullptr);
        ASSERT_GT(count, 0u);
        if (rounds == 0) { EXPECT_EQ(count, 2u); } // duplicate requested root collapses
        largest_frontier = std::max(largest_frontier, count);
        for (size_t i = 0; i < count; ++i) {
            if (i) { EXPECT_LT(std::memcmp(&frontier[i - 1u], &frontier[i], sizeof(hash128_t)), 0); }
            EXPECT_FALSE(same(frontier[i], first.entity_id));
            EXPECT_FALSE(same(frontier[i], child));
            for (const auto& tag : source.basis.tags) EXPECT_FALSE(same(frontier[i], tag));
            for (const auto& number : source.basis.byte_numbers) EXPECT_FALSE(same(frontier[i], number));
            for (const auto& old : requested) EXPECT_FALSE(same(frontier[i], old));
            requested.push_back(frontier[i]);
            hydrated.add(source, frontier[i]);
        }
        EXPECT_LE(physicality_descriptor_readback_bytes(result.get()),
            physicality_descriptor_readback_peak_bytes(result.get()));
        EXPECT_LE(physicality_descriptor_readback_peak_bytes(result.get()), kBytes);
    }
    EXPECT_GT(rounds, 1u);
    EXPECT_LT(rounds, source.nodes.size());
    EXPECT_GT(largest_frontier, 2u);
}

TEST(PhysicalityDescriptorFrontier, AcceptsDuplicateHydratedRecordsAndPreservesFactorAndNullBits) {
    const float factors[]{0.25f, -0.75f, 0.5f, 0.125f, 0.875f, -0.625f};
    double packed[4]{};
    size_t vertices = 0;
    ASSERT_EQ(laplace_factor_pack_values(factors, 6, packed, &vertices), 0);
    auto factor = body();
    factor.trajectory_xyzm = packed;
    factor.trajectory_vertices = vertices;
    factor.n_constituents = 1;
    auto empty = body();
    empty.type = 8;
    const std::vector<physicality_descriptor_input_t> inputs{factor, empty, factor};
    Catalog source(inputs);
    auto hydrated = all(source);
    hydrated.add(source, source.nodes.front().id);
    std::reverse(hydrated.nodes.begin(), hydrated.nodes.end());
    physicality_descriptor_readback_t* raw = nullptr;
    ASSERT_EQ(hydrated.prepare(source, &raw), PHYSICALITY_DESCRIPTOR_OK);
    Readback result(raw, physicality_descriptor_readback_free);
    size_t count = 0;
    const auto* decoded = physicality_descriptor_readback_inputs(result.get(), &count);
    ASSERT_EQ(count, inputs.size());
    for (size_t i = 0; i < count; ++i) expect_body(decoded[i], inputs[i]);
}

TEST(PhysicalityDescriptorFrontier, RejectsTerminalVocabularyInTypedSlotsBeforeRequestingIt) {
    for (const bool numeral : {false, true}) {
        Catalog source({body()});
        auto* root = const_cast<physicality_descriptor_node_t*>(source.find(source.roots[0]));
        ASSERT_NE(root, nullptr);
        source.children[root->first_child + 2u] = numeral ? source.basis.byte_numbers[1] :
            source.basis.tags[PHYSICALITY_DESCRIPTOR_ABSENT];
        source.rehash();
        Hydrated hydrated;
        hydrated.add(source, source.roots[0]);
        physicality_descriptor_readback_t* raw = nullptr;
        EXPECT_EQ(hydrated.prepare(source, &raw), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
        EXPECT_EQ(raw, nullptr);
    }
}

TEST(PhysicalityDescriptorFrontier, AuthenticRootFilterIsOnlyACandidateUntilTypedFieldsValidate) {
    Catalog source({body()});
    for (const auto& node : source.nodes)
        if (same(source.children[node.first_child], source.basis.tags[PHYSICALITY_DESCRIPTOR_BINARY64])) {
            source.children[node.first_child] = source.basis.tags[PHYSICALITY_DESCRIPTOR_U64];
            break;
        }
    source.rehash();
    const auto* root = source.find(source.roots[0]);
    ASSERT_NE(root, nullptr);
    hash128_t entity{99, 100};
    EXPECT_EQ(physicality_descriptor_readback_root_entity(root, source.children.data(),
        source.children.size(), &source.basis, &entity), 1);
    EXPECT_TRUE(same(entity, body().entity_id));
    auto hydrated = all(source);
    physicality_descriptor_readback_t* raw = nullptr;
    EXPECT_EQ(hydrated.prepare(source, &raw), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(raw, nullptr);
    auto forged = *root;
    forged.id.lo ^= 1u;
    const hash128_t sentinel{99, 100};
    entity = sentinel;
    EXPECT_EQ(physicality_descriptor_readback_root_entity(&forged, source.children.data(),
        source.children.size(), &source.basis, &entity), 0);
    EXPECT_TRUE(same(entity, sentinel));
}

TEST(PhysicalityDescriptorFrontier, InvalidSuppliedRecordWinsOverOtherMissingRoots) {
    Catalog source({body()});
    Hydrated hydrated;
    hydrated.add(source, source.roots[0]);
    hydrated.nodes[0].id.lo ^= 1u;
    source.roots.push_back(hash128_t{91, 92});
    physicality_descriptor_readback_t* raw = nullptr;
    EXPECT_EQ(hydrated.prepare(source, &raw), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(raw, nullptr);
    hydrated.nodes[0].first_child = SIZE_MAX;
    EXPECT_EQ(hydrated.prepare(source, &raw), PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(raw, nullptr);
}

TEST(PhysicalityDescriptorFrontier, EnforcesDiscoveryPeakAndSeparateReplanGrantWithoutPartialBody) {
    Catalog source({body()});
    Hydrated hydrated;
    hydrated.add(source, source.roots[0]);
    physicality_descriptor_readback_t* raw = nullptr;
    ASSERT_EQ(hydrated.prepare(source, &raw), PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    Readback pending(raw, physicality_descriptor_readback_free);
    const size_t peak = physicality_descriptor_readback_peak_bytes(pending.get());
    ASSERT_GT(peak, physicality_descriptor_readback_bytes(pending.get()));
    raw = nullptr;
    EXPECT_EQ(hydrated.prepare(source, &raw, peak - 1u), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(raw, nullptr);
    auto complete = all(source);
    EXPECT_EQ(complete.prepare(source, &raw, kBytes, SIZE_MAX, 0), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(raw, nullptr);
    ASSERT_EQ(complete.prepare(source, &raw), PHYSICALITY_DESCRIPTOR_OK);
    Readback result(raw, physicality_descriptor_readback_free);
    EXPECT_LE(physicality_descriptor_readback_peak_bytes(result.get()), kBytes);
}

TEST(PhysicalityDescriptorFrontier, ChargesExpandedContentHashBeforeReplanningTheExactBody) {
    const hash128_t child{71, 72};
    std::array<hash128_t, 7> constituents;
    constituents.fill(child);
    mantissa_payload_t payload{};
    payload.entity_id = child;
    payload.run_length = 7;
    payload.flags = laplace_vertex_flags(1, 0, 0);
    double trajectory[4]{};
    mantissa_pack(trajectory, &payload);
    auto content = body();
    content.type = 1;
    hash128_merkle(0, constituents.data(), constituents.size(), &content.entity_id);
    content.trajectory_xyzm = trajectory;
    content.trajectory_vertices = 1;
    content.n_constituents = 7;
    Catalog source({content, content});
    auto hydrated = all(source);
    physicality_descriptor_readback_t* raw = nullptr;
    EXPECT_EQ(hydrated.prepare(source, &raw, kBytes, 13), PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(raw, nullptr);
    ASSERT_EQ(hydrated.prepare(source, &raw, kBytes, 14), PHYSICALITY_DESCRIPTOR_OK);
    Readback result(raw, physicality_descriptor_readback_free);
    EXPECT_EQ(physicality_descriptor_readback_content_hash_operands(result.get()), 14u);
}

} // namespace
