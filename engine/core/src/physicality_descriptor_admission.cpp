#include "physicality_descriptor_provider.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <memory_resource>
#include <new>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/math4d.h"
#include "laplace/core/trajectory.h"

namespace {

struct Status { physicality_descriptor_status_t value; };

class Memory final : public std::pmr::memory_resource {
public:
    explicit Memory(size_t limit) : limit_(limit) {}
    size_t used() const { return used_; }
    size_t peak() const { return peak_; }
    size_t remaining() const { return limit_ - used_; }
    void claim(size_t bytes) {
        if (bytes > remaining()) throw std::bad_alloc();
        used_ += bytes;
        peak_ = std::max(peak_, used_);
    }
    void release(size_t bytes) { used_ -= bytes; }
private:
    void* do_allocate(size_t bytes, size_t alignment) override {
        claim(bytes);
        try { return ::operator new(bytes, std::align_val_t(alignment)); }
        catch (...) { release(bytes); throw; }
    }
    void do_deallocate(void* pointer, size_t bytes, size_t alignment) override {
        ::operator delete(pointer, std::align_val_t(alignment));
        release(bytes);
    }
    bool do_is_equal(const std::pmr::memory_resource& other) const noexcept override {
        return this == &other;
    }
    size_t limit_;
    size_t used_ = 0;
    size_t peak_ = 0;
};

struct IdHash {
    size_t operator()(const hash128_t& id) const noexcept {
        uint64_t x = id.lo ^ id.hi;
        x ^= x >> 33;
        x *= UINT64_C(0xff51afd7ed558ccd);
        return static_cast<size_t>(x ^ (x >> 33));
    }
};
struct IdEqual {
    bool operator()(const hash128_t& a, const hash128_t& b) const noexcept {
        return hash128_equals(&a, &b) != 0;
    }
};
template<class T> using IdMap = std::pmr::unordered_map<hash128_t,T,IdHash,IdEqual>;
using IdSet = std::pmr::unordered_set<hash128_t,IdHash,IdEqual>;

template<class T, void (*Destroy)(T*)> struct External {
    T* value = nullptr;
    Memory* memory;
    size_t bytes = 0;
    explicit External(Memory& owner) : memory(&owner) {}
    void account(size_t amount) { memory->claim(amount); bytes = amount; }
    ~External() { Destroy(value); memory->release(bytes); }
};

struct Geometry {
    hash128_t id{};
    std::array<double,4> coord{};
    hilbert128_t hilbert{};
};

Geometry geometry(const tier_node_view_t& node) {
    Geometry value;
    value.id = node.id;
    std::copy_n(node.coord, 4, value.coord.data());
    value.hilbert = node.hilbert;
    return value;
}

struct Selected {
    Geometry physicality;
    size_t body_index = SIZE_MAX; // SIZE_MAX means actual mapped codepoint floor.
};

struct OutputNode {
    Geometry geometry;
    size_t first_child;
    size_t child_count;
};

void require(bool condition, physicality_descriptor_status_t status = PHYSICALITY_DESCRIPTOR_INVALID_BODY) {
    if (!condition) throw Status{status};
}

} // namespace

struct physicality_descriptor_materialization {
    Memory memory;
    std::pmr::vector<hash128_t> pending;
    std::pmr::vector<physicality_descriptor_admitted_form_t> forms;
    intent_stage_t* stage = nullptr;
    size_t stage_bytes = 0;
    explicit physicality_descriptor_materialization(size_t ceiling)
        : memory(ceiling - sizeof(*this)), pending(&memory), forms(&memory) {}
    ~physicality_descriptor_materialization() { intent_stage_free(stage); }
};

namespace {

physicality_descriptor_status_t materialize(
    physicality_descriptor_materialization& result,
    const physicality_descriptor_capture_t* source,
    const physicality_descriptor_vocabulary_t& vocabulary,
    const intent_stage_t* const* current_stages, size_t stage_count,
    const intent_stage_t* const* admitted_stages, size_t admitted_stage_count,
    const hash128_t* missing_ids, size_t missing_count,
    const physicality_descriptor_source_observation_t* sources, size_t source_count,
    const hash128_t& generated_source, int64_t generated_at) {
    Memory& memory = result.memory;
    size_t original_count = 0;
    const auto* original = physicality_descriptor_capture_inputs(source, &original_count);
    const auto* observations = physicality_descriptor_capture_observations(source, nullptr);
    require(source_count == original_count, PHYSICALITY_DESCRIPTOR_INVALID);
    if (original_count == 0u) {
        result.stage = intent_stage_new_bounded(0u, memory.remaining());
        require(result.stage != nullptr, PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
        result.stage_bytes = intent_stage_memory_bytes(result.stage);
        memory.claim(result.stage_bytes);
        return PHYSICALITY_DESCRIPTOR_OK;
    }
    for (size_t i = 0; i < source_count; ++i)
        require(std::isfinite(sources[i].source_trust) && sources[i].source_trust >= 0.0 &&
            sources[i].source_trust <= 1.0, PHYSICALITY_DESCRIPTOR_INVALID);

    External<physicality_descriptor_capture_t, physicality_descriptor_capture_free> current(memory);
    physicality_descriptor_limits_t limits{memory.remaining()};
    if (stage_count != 0) {
        const auto status = physicality_descriptor_capture_stages(current_stages, stage_count,
            &vocabulary.basis, &limits, memory.remaining(), &current.value);
        require(status == PHYSICALITY_DESCRIPTOR_OK, status);
        current.account(physicality_descriptor_capture_bytes(current.value));
    }
    size_t current_count = 0;
    const auto* current_inputs = physicality_descriptor_capture_inputs(current.value, &current_count);
    External<physicality_descriptor_capture_t, physicality_descriptor_capture_free> admitted(memory);
    limits.maximum_plan_bytes = memory.remaining();
    if (admitted_stage_count != 0) {
        const auto status = physicality_descriptor_capture_stages(admitted_stages, admitted_stage_count,
            &vocabulary.basis, &limits, memory.remaining(), &admitted.value);
        require(status == PHYSICALITY_DESCRIPTOR_OK, status);
        admitted.account(physicality_descriptor_capture_bytes(admitted.value));
    }
    size_t admitted_count = 0;
    const auto* admitted_inputs = physicality_descriptor_capture_inputs(admitted.value, &admitted_count);
    require(current_count <= SIZE_MAX - original_count &&
        admitted_count <= SIZE_MAX - original_count - current_count, PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    std::pmr::vector<physicality_descriptor_input_t> inputs(&memory);
    inputs.reserve(original_count + current_count + admitted_count);
    if (original_count != 0) inputs.insert(inputs.end(), original, original + original_count);
    for (size_t i = 0; i < current_count; ++i) {
        require(current_inputs[i].type == 1);
        inputs.push_back(current_inputs[i]);
    }
    for (size_t i = 0; i < admitted_count; ++i) {
        require(admitted_inputs[i].type == 1);
        inputs.push_back(admitted_inputs[i]);
    }

    External<physicality_descriptor_plan_t, physicality_descriptor_plan_free> plan(memory);
    limits.maximum_plan_bytes = memory.remaining();
    const auto planned = physicality_descriptor_plan_build(inputs.data(), inputs.size(),
        &vocabulary.basis, &limits, &plan.value);
    require(planned == PHYSICALITY_DESCRIPTOR_OK, planned);
    plan.account(physicality_descriptor_plan_bytes(plan.value));
    const hash128_t* descriptors = physicality_descriptor_plan_roots(plan.value, nullptr);

    IdMap<size_t> current_by_entity(&memory), admitted_by_entity(&memory), body_by_descriptor(&memory);
    for (size_t i = 0; i < inputs.size(); ++i) {
        body_by_descriptor.emplace(descriptors[i], i);
        if (i < original_count) continue;
        auto& index = i < original_count + current_count ? current_by_entity : admitted_by_entity;
        const auto found = index.find(inputs[i].entity_id);
        if (found == index.end()) index.emplace(inputs[i].entity_id, i);
        else require(hash128_equals(&descriptors[i], &descriptors[found->second]));
    }
    IdSet missing(&memory), pending(&memory);
    for (size_t i = 0; i < missing_count; ++i) {
        require(current_by_entity.find(missing_ids[i]) == current_by_entity.end());
        missing.insert(missing_ids[i]);
    }
    IdMap<Selected> selected(&memory);
    size_t reference_count = 0;
    const auto* references = physicality_descriptor_plan_references(plan.value, &reference_count);
    for (size_t i = 0; i < reference_count; ++i) {
        if (references[i].kind != PHYSICALITY_DESCRIPTOR_CARRIER_ENTITY) continue;
        const hash128_t entity = references[i].entity_id;
        if (selected.find(entity) != selected.end() || pending.find(entity) != pending.end()) continue;
        Selected selection;
        auto found = current_by_entity.find(entity);
        if (found != current_by_entity.end()) selection.body_index = found->second;
        else {
            uint32_t codepoint;
            if (codepoint_table_lookup_id(&entity, &codepoint) == 0) {
                require(codepoint_table_resolve_atom(codepoint, &selection.physicality.id,
                    selection.physicality.coord.data(), &selection.physicality.hilbert) == 0 &&
                    hash128_equals(&selection.physicality.id, &entity));
                selected.emplace(entity, selection);
                continue;
            }
            if (missing.find(entity) == missing.end()) {
                pending.insert(entity);
                continue;
            }
            found = admitted_by_entity.find(entity);
            require(found != admitted_by_entity.end(),
                static_cast<physicality_descriptor_status_t>(PHYSICALITY_DESCRIPTOR_MISSING_REFERENCE));
            selection.body_index = found->second;
        }
        const auto& body = inputs[selection.body_index];
        selection.physicality.id = body.entity_id;
        std::copy_n(body.coord, 4, selection.physicality.coord.data());
        selection.physicality.hilbert = body.hilbert_index;
        selected.emplace(entity, selection);
    }
    if (!pending.empty()) {
        result.pending.assign(pending.begin(), pending.end());
        std::sort(result.pending.begin(), result.pending.end(), [](const hash128_t& a, const hash128_t& b) {
            return hash128_compare(&a, &b) < 0;
        });
        return static_cast<physicality_descriptor_status_t>(PHYSICALITY_DESCRIPTOR_NEEDS_PROVIDER);
    }

    IdMap<Geometry> geometries(&memory);
    for (const auto& tag : vocabulary.tags) geometries.emplace(tag.id, geometry(tag));
    for (const auto& number : vocabulary.numbers) geometries.emplace(number.id, geometry(number));
    for (const auto* tag : {&vocabulary.view_schema, &vocabulary.view_recipe,
            &vocabulary.floor_schema, &vocabulary.selection_schema, &vocabulary.scope_schema,
            &vocabulary.context_schema, &vocabulary.source_schema, &vocabulary.unit_schema})
        geometries.emplace(tag->id, geometry(*tag));
    std::pmr::vector<OutputNode> output(&memory);
    std::pmr::vector<hash128_t> output_children(&memory);
    std::pmr::vector<double> child_coordinates(&memory);
    std::pmr::vector<std::byte> centroid_workspace(&memory);
    std::pmr::vector<hash128_t> fields(&memory);
    size_t node_count = 0;
    const auto* nodes = physicality_descriptor_plan_nodes(plan.value, &node_count);
    const auto* children = physicality_descriptor_plan_children(plan.value, nullptr);

    auto compose = [&](const hash128_t* operands, size_t count, const double* reference_coord,
                       size_t reference_index) -> Geometry {
        require(count >= 2 && count <= INT32_MAX && count <= UINT32_MAX && count <= SIZE_MAX / 4u,
            PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
        child_coordinates.resize(count * 4u);
        for (size_t child = 0; child < count; ++child) {
            const double* coord;
            if (child == reference_index) coord = reference_coord;
            else {
                const auto found = geometries.find(operands[child]);
                require(found != geometries.end(),
                    static_cast<physicality_descriptor_status_t>(PHYSICALITY_DESCRIPTOR_MISSING_REFERENCE));
                coord = found->second.coord.data();
            }
            std::copy_n(coord, 4, child_coordinates.data() + child * 4u);
        }
        Geometry value;
        /* The descriptor is a typed document record. This storage floor is
         * independent of recursive graph depth and does not enter identity. */
        size_t workspace_bytes;
        require(math4d_centroid_workspace_size(count, &workspace_bytes) == 0,
            PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
        centroid_workspace.resize(workspace_bytes);
        require(hash_composer_compose_node_with_workspace(4, operands, child_coordinates.data(), count,
            centroid_workspace.data(), centroid_workspace.size(),
            &value.id, value.coord.data(), &value.hilbert) == 0);
        const auto existing = geometries.find(value.id);
        if (existing != geometries.end()) {
            require(std::memcmp(existing->second.coord.data(), value.coord.data(), 4u * sizeof(double)) == 0);
            return existing->second;
        }
        const size_t start = output_children.size();
        output_children.insert(output_children.end(), operands, operands + count);
        output.push_back(OutputNode{value, start, count});
        geometries.emplace(value.id, value);
        return value;
    };

    for (size_t i = 0; i < node_count; ++i) {
        const auto& node = nodes[i];
        const hash128_t* operands = children + node.first_child;
        const double* reference_coord = nullptr;
        size_t reference_index = SIZE_MAX;
        if (node.child_count == 9u && hash128_equals(&operands[0],
                &vocabulary.basis.tags[PHYSICALITY_DESCRIPTOR_SCHEMA])) {
            const auto found = body_by_descriptor.find(node.id);
            require(found != body_by_descriptor.end());
            reference_coord = inputs[found->second].coord;
            reference_index = 1u;
        } else if (node.child_count == 5u && hash128_equals(&operands[0],
                &vocabulary.basis.tags[PHYSICALITY_DESCRIPTOR_CARRIER])) {
            const auto found = selected.find(operands[1]);
            require(found != selected.end());
            reference_coord = found->second.physicality.coord.data();
            reference_index = 1u;
        }
        const auto produced = compose(operands, node.child_count, reference_coord, reference_index);
        require(hash128_equals(&produced.id, &node.id));
    }

    std::array<hash128_t,17> floor_fields{};
    floor_fields[0] = vocabulary.floor_schema.id;
    const auto* fingerprint = reinterpret_cast<const uint8_t*>(&vocabulary.floor_receipt);
    for (size_t i = 0; i < 16u; ++i) floor_fields[i + 1u] = vocabulary.basis.byte_numbers[fingerprint[i]];
    const auto floor_receipt = compose(floor_fields.data(), floor_fields.size(), nullptr, SIZE_MAX);
    IdMap<hash128_t> views(&memory);
    IdMap<const physicality_descriptor_node_t*> node_by_id(&memory);
    for (size_t i = 0; i < node_count; ++i) node_by_id.emplace(nodes[i].id, &nodes[i]);
    std::pmr::vector<size_t> reachable(&memory);
    std::pmr::vector<hash128_t> scope_fields(&memory);
    IdSet reached_descriptors(&memory);
    auto trajectory_for_input = [&](size_t input_index) {
        const auto root = node_by_id.find(descriptors[input_index]);
        require(root != node_by_id.end());
        const auto found = node_by_id.find(children[root->second->first_child + 5u]);
        require(found != node_by_id.end());
        return found->second;
    };
    for (size_t input_index = 0; input_index < inputs.size(); ++input_index) {
        if (views.find(descriptors[input_index]) != views.end()) continue;
        /* The finite recipe records every selected body reachable from this
         * source form. A grandchild replacement must change the view receipt;
         * an unrelated batch neighbor must not. Cycles terminate on exact
         * body identity, never by an arbitrary recursion depth. */
        reachable.clear();
        reached_descriptors.clear();
        scope_fields.clear();
        reachable.push_back(input_index);
        for (size_t next = 0; next < reachable.size(); ++next) {
            const size_t body_index = reachable[next];
            const auto* body_trajectory = trajectory_for_input(body_index);
            for (size_t vertex = 0; vertex < inputs[body_index].trajectory_vertices; ++vertex) {
                const auto carrier = node_by_id.find(children[body_trajectory->first_child + vertex + 1u]);
                require(carrier != node_by_id.end());
                const hash128_t* carrier_fields = children + carrier->second->first_child;
                if (hash128_equals(&carrier_fields[0], &vocabulary.basis.tags[PHYSICALITY_DESCRIPTOR_FACTOR])) continue;
                const auto chosen = selected.find(carrier_fields[1]);
                require(chosen != selected.end());
                const size_t selected_index = chosen->second.body_index;
                if (selected_index != SIZE_MAX && reached_descriptors.insert(descriptors[selected_index]).second)
                    reachable.push_back(selected_index);
            }
        }
        scope_fields.assign(reached_descriptors.begin(), reached_descriptors.end());
        std::sort(scope_fields.begin(), scope_fields.end(), [](const hash128_t& a, const hash128_t& b) {
            return hash128_compare(&a, &b) < 0;
        });
        scope_fields.insert(scope_fields.begin(), vocabulary.scope_schema.id);
        if (scope_fields.size() == 1u) scope_fields.push_back(vocabulary.basis.tags[PHYSICALITY_DESCRIPTOR_ABSENT]);
        const auto scope = compose(scope_fields.data(), scope_fields.size(), nullptr, SIZE_MAX);
        const auto* trajectory = trajectory_for_input(input_index);
        fields.clear();
        fields.push_back(vocabulary.view_schema.id);
        fields.push_back(descriptors[input_index]);
        fields.push_back(vocabulary.view_recipe.id);
        fields.push_back(floor_receipt.id);
        fields.push_back(scope.id);
        for (size_t vertex = 0; vertex < inputs[input_index].trajectory_vertices; ++vertex) {
            const hash128_t carrier_id = children[trajectory->first_child + vertex + 1u];
            const auto carrier = node_by_id.find(carrier_id);
            require(carrier != node_by_id.end());
            const hash128_t* carrier_fields = children + carrier->second->first_child;
            if (hash128_equals(&carrier_fields[0], &vocabulary.basis.tags[PHYSICALITY_DESCRIPTOR_FACTOR]))
                continue;
            const auto found = selected.find(carrier_fields[1]);
            require(found != selected.end());
            const auto& selection = found->second;
            const hash128_t selected_form = selection.body_index == SIZE_MAX ? floor_receipt.id :
                descriptors[selection.body_index];
            const std::array<hash128_t,5> binding{vocabulary.selection_schema.id,
                carrier_id, carrier_fields[1], selected_form, scope.id};
            const auto receipt = compose(binding.data(), binding.size(), selection.physicality.coord.data(), 2u);
            fields.push_back(receipt.id);
        }
        const auto view = compose(fields.data(), fields.size(), nullptr, SIZE_MAX);
        views.emplace(descriptors[input_index], view.id);
    }
    result.forms.resize(original_count);
    for (size_t i = 0; i < original_count; ++i)
        result.forms[i] = {descriptors[i], views.at(descriptors[i])};

    hash128_t relation;
    require(laplace_relation_resolve("HAS_PHYSICALITY", &relation) == 0);
    std::pmr::vector<laplace_attestation_staged_t> attestations(&memory);
    IdMap<size_t> observation_index(&memory);
    auto identifier = [&](const hash128_t& schema, const hash128_t& exact_value) {
        std::array<hash128_t,17> operands;
        operands[0] = schema;
        const auto* octets = reinterpret_cast<const uint8_t*>(&exact_value);
        for (size_t byte = 0u; byte < 16u; ++byte)
            operands[byte + 1u] = vocabulary.basis.byte_numbers[octets[byte]];
        return compose(operands.data(), operands.size(), nullptr, SIZE_MAX);
    };
    for (size_t i = 0; i < original_count; ++i) {
        /* A source-unit receipt is a typed identifier, not automatically an E.
         * Its ordinary context binds the registered source id and exact unit
         * receipt bytes while the attestation retains the real source owner. */
        const auto source_identifier = identifier(vocabulary.source_schema.id, sources[i].source_id);
        const auto unit_identifier = identifier(vocabulary.unit_schema.id, sources[i].source_unit_id);
        const std::array<hash128_t,3> context_fields{vocabulary.context_schema.id,
            source_identifier.id, unit_identifier.id};
        const auto context = compose(context_fields.data(), context_fields.size(), nullptr, SIZE_MAX);
        laplace_attestation_staged_t observation{};
        require(laplace_attestation_resolved_build(&inputs[i].entity_id, &relation,
            &descriptors[i], 0, &sources[i].source_id, &context.id, 0,
            sources[i].source_trust, 1, 1, observations[i].observed_at_unix_us, &observation) == 0);
        observation.last_observed_at_unix_us = observations[i].observed_at_unix_us;
        /* The ordinary writer's source-unit journal owns replay exclusion.
         * fold_replayable stays on its ordinary transport law: the alternate
         * mode requires an atomic consensus transaction participant. */
        const auto old = observation_index.find(observation.id);
        if (old != observation_index.end()) {
            auto& retained = attestations[old->second];
            require(retained.opponent_rd_fp1e9 == observation.opponent_rd_fp1e9 &&
                retained.opponent_rating_fp1e9 == observation.opponent_rating_fp1e9);
            retained.last_observed_at_unix_us = std::max(retained.last_observed_at_unix_us,
                observation.last_observed_at_unix_us);
        } else {
            observation_index.emplace(observation.id, attestations.size());
            attestations.push_back(observation);
        }
    }

    size_t widest = 0;
    for (const auto& node : output) widest = std::max(widest, node.child_count);
    std::pmr::vector<double> packed(&memory);
    require(widest <= SIZE_MAX / 4u, PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    packed.resize(widest * 4u);
    std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)> stage(
        intent_stage_new_bounded(0, memory.remaining()), intent_stage_free);
    require(stage != nullptr, PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    const auto document_type = laplace_content_tier_type_id(4);
    for (const auto& node : output) {
        hash128_t placement;
        laplace_physicality_id_compute(node.geometry.id, 1, &placement);
        require(trajectory_build(output_children.data() + node.first_child, node.child_count, packed.data()) == 0);
        require(intent_stage_add_entity(stage.get(), &node.geometry.id, 4, &document_type, &generated_source) == 0 &&
            intent_stage_add_physicality(stage.get(), &placement, &node.geometry.id, 1,
                node.geometry.coord.data(), &node.geometry.hilbert, packed.data(),
                static_cast<uint32_t>(node.child_count), static_cast<int32_t>(node.child_count),
                1, 0.0, 1, 0, generated_at) == 0,
            PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    }
    require(laplace_attestation_staged_batch_add(stage.get(), attestations.data(), attestations.size(), nullptr) == 0,
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    result.stage_bytes = intent_stage_memory_bytes(stage.get());
    const size_t stage_peak = intent_stage_memory_peak_bytes(stage.get());
    memory.claim(stage_peak);
    memory.release(stage_peak - result.stage_bytes);
    result.stage = stage.release();
    return PHYSICALITY_DESCRIPTOR_OK;
}

} // namespace

extern "C" physicality_descriptor_status_t physicality_descriptor_materialize(
    const physicality_descriptor_capture_t* captured_source,
    const physicality_descriptor_vocabulary_t* vocabulary,
    const intent_stage_t* const* current_content_stages, size_t current_stage_count,
    const intent_stage_t* const* admitted_content_stages, size_t admitted_stage_count,
    const hash128_t* explicitly_missing_ids, size_t missing_count,
    const physicality_descriptor_source_observation_t* observation_sources, size_t observation_source_count,
    const hash128_t* source_id, int64_t observed_at_unix_us, size_t maximum_bytes,
    physicality_descriptor_materialization_t** out_materialization) {
    if (out_materialization == nullptr) return PHYSICALITY_DESCRIPTOR_INVALID;
    *out_materialization = nullptr;
    if (captured_source == nullptr || vocabulary == nullptr || source_id == nullptr ||
        (current_stage_count != 0 && current_content_stages == nullptr) ||
        (admitted_stage_count != 0 && admitted_content_stages == nullptr) ||
        (missing_count != 0 && explicitly_missing_ids == nullptr) ||
        (observation_source_count != 0 && observation_sources == nullptr))
        return PHYSICALITY_DESCRIPTOR_INVALID;
    if (maximum_bytes < sizeof(physicality_descriptor_materialization))
        return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    hash128_t current_floor;
    if (codepoint_table_copy_receipt(&current_floor) != 0)
        return static_cast<physicality_descriptor_status_t>(PHYSICALITY_DESCRIPTOR_MISSING_FLOOR);
    if (!hash128_equals(&current_floor, &vocabulary->floor_receipt)) return PHYSICALITY_DESCRIPTOR_INVALID;
    if (!codepoint_table_id_index_ready()) return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED;
    try {
        auto result = std::make_unique<physicality_descriptor_materialization>(maximum_bytes);
        const auto status = materialize(*result, captured_source, *vocabulary,
            current_content_stages, current_stage_count, admitted_content_stages, admitted_stage_count,
            explicitly_missing_ids, missing_count,
            observation_sources, observation_source_count, *source_id, observed_at_unix_us);
        *out_materialization = result.release();
        return status;
    } catch (const Status& status) { return status.value; }
      catch (const std::bad_alloc&) { return PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED; }
}

extern "C" void physicality_descriptor_materialization_free(physicality_descriptor_materialization_t* value) {
    delete value;
}

extern "C" const hash128_t* physicality_descriptor_materialization_pending(
    const physicality_descriptor_materialization_t* value, size_t* count) {
    if (count != nullptr) *count = value == nullptr ? 0u : value->pending.size();
    return value == nullptr ? nullptr : value->pending.data();
}

extern "C" const physicality_descriptor_admitted_form_t* physicality_descriptor_materialization_forms(
    const physicality_descriptor_materialization_t* value, size_t* count) {
    if (count != nullptr) *count = value == nullptr ? 0u : value->forms.size();
    return value == nullptr ? nullptr : value->forms.data();
}

extern "C" intent_stage_t* physicality_descriptor_materialization_take_stage(
    physicality_descriptor_materialization_t* value) {
    if (value == nullptr) return nullptr;
    intent_stage_t* stage = value->stage;
    value->stage = nullptr;
    value->memory.release(value->stage_bytes);
    value->stage_bytes = 0;
    return stage;
}

extern "C" size_t physicality_descriptor_materialization_bytes(
    const physicality_descriptor_materialization_t* value) {
    return value == nullptr ? 0u : sizeof(*value) + value->memory.used();
}

extern "C" size_t physicality_descriptor_materialization_peak_bytes(
    const physicality_descriptor_materialization_t* value) {
    return value == nullptr ? 0u : sizeof(*value) + value->memory.peak();
}
