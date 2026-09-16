#include <gtest/gtest.h>

#include <array>
#include <cstring>
#include <limits>
#include <memory>
#include <string>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/physicality_descriptor_admission.h"

namespace {

using Stage = std::unique_ptr<intent_stage_t, decltype(&intent_stage_free)>;
using Vocabulary = std::unique_ptr<physicality_descriptor_vocabulary_t,
    decltype(&physicality_descriptor_vocabulary_free)>;
using Capture = std::unique_ptr<physicality_descriptor_capture_t,
    decltype(&physicality_descriptor_capture_free)>;

constexpr size_t kVocabularyBudget = 8u * 1024u * 1024u;
const hash128_t kSource{0x1234, 0x5678};

// Public schema spellings are tested through the ordinary text owner. The
// fixture supplies no test-only identity implementation or vocabulary ids.
const std::array<const char*, PHYSICALITY_DESCRIPTOR_TAG_COUNT> kTags{
    "PhysicalityDescriptorV1", "PhysicalityCoordinateV1", "PhysicalityHilbertV1",
    "PhysicalityTrajectoryV1", "PhysicalityCarrierV1", "PhysicalityFactorV1",
    "PhysicalityAbsentV1", "PhysicalityPresentV1", "PhysicalityI16LEV1",
    "PhysicalityI32LEV1", "PhysicalityU16LEV1", "PhysicalityU64LEV1",
    "PhysicalityBinary64LEV1"
};

Vocabulary vocabulary() {
    physicality_descriptor_vocabulary_t* raw = nullptr;
    EXPECT_EQ(PHYSICALITY_DESCRIPTOR_OK, physicality_descriptor_vocabulary_create(
        &kSource, kVocabularyBudget, &raw));
    return Vocabulary(raw, physicality_descriptor_vocabulary_free);
}

physicality_descriptor_input_t body() {
    physicality_descriptor_input_t input{};
    input.entity_id = hash128_t{0xabc, 0xdef};
    input.type = 3;
    input.coord[0] = 0.25;
    input.coord[1] = -0.5;
    input.coord[2] = 0.125;
    input.coord[3] = 0.75;
    input.alignment_residual_is_null = 1;
    input.source_dim_is_null = 1;
    return input;
}

int add_body(intent_stage_t* stage, const physicality_descriptor_input_t& input,
             int64_t observed_at = 0) {
    hash128_t placement{};
    laplace_physicality_id_compute(input.entity_id, input.type, &placement);
    return intent_stage_add_physicality(stage, &placement, &input.entity_id, input.type,
        input.coord, &input.hilbert_index, input.trajectory_xyzm,
        static_cast<uint32_t>(input.trajectory_vertices), input.n_constituents,
        input.alignment_residual_is_null, input.alignment_residual,
        input.source_dim_is_null, input.source_dim, observed_at);
}

int add_entity(intent_stage_t* stage) {
    const hash128_t type = laplace_content_tier_type_id(2);
    return intent_stage_add_entity(stage, &kSource, 2, &type, nullptr);
}

int add_attestation(intent_stage_t* stage) {
    const hash128_t relation{0x8765, 0x4321};
    return intent_stage_add_attestation(stage, &kSource, &kSource, &relation,
        nullptr, &kSource, nullptr, 2, 0, 1, 0, 350000000000LL, 1500000000000LL, nullptr);
}

TEST(PhysicalityDescriptorStageAllocation, EmptyAndHintAllocationsRespectTheDeclaredCeiling) {
    Stage empty(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(empty, nullptr);
    const size_t empty_bytes = intent_stage_memory_bytes(empty.get());
    ASSERT_GT(empty_bytes, 0u);
    EXPECT_EQ(intent_stage_new_bounded(0, empty_bytes - 1u), nullptr);
    Stage exact(intent_stage_new_bounded(0, empty_bytes), intent_stage_free);
    ASSERT_NE(exact, nullptr);
    EXPECT_EQ(intent_stage_memory_bytes(exact.get()), empty_bytes);
    EXPECT_FALSE(intent_stage_allocation_failed(exact.get()));
    EXPECT_NE(add_entity(exact.get()), 0);
    EXPECT_TRUE(intent_stage_allocation_failed(exact.get()));
    EXPECT_EQ(intent_stage_memory_bytes(exact.get()), empty_bytes);
    EXPECT_EQ(intent_stage_entity_count(exact.get()), 0u);

    Stage hinted(intent_stage_new(11), intent_stage_free);
    ASSERT_NE(hinted, nullptr);
    const size_t hinted_bytes = intent_stage_memory_bytes(hinted.get());
    EXPECT_GT(hinted_bytes, empty_bytes);
    Stage bounded_hint(intent_stage_new_bounded(11, hinted_bytes), intent_stage_free);
    ASSERT_NE(bounded_hint, nullptr);
    EXPECT_EQ(intent_stage_memory_bytes(bounded_hint.get()), hinted_bytes);
    EXPECT_EQ(intent_stage_new_bounded(SIZE_MAX, SIZE_MAX), nullptr);
}

TEST(PhysicalityDescriptorStageAllocation, AllCopyBuffersShareOneRetainedBudget) {
    Stage reference(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(reference, nullptr);
    const size_t empty_bytes = intent_stage_memory_bytes(reference.get());
    ASSERT_EQ(add_entity(reference.get()), 0);
    ASSERT_EQ(add_body(reference.get(), body()), 0);
    ASSERT_EQ(add_attestation(reference.get()), 0);
    size_t payload_bytes = 0;
    for (const auto table : {INTENT_STAGE_TABLE_ENTITIES,
             INTENT_STAGE_TABLE_PHYSICALITIES, INTENT_STAGE_TABLE_ATTESTATIONS}) {
        size_t bytes = 0;
        ASSERT_NE(intent_stage_tuple_ptr(reference.get(), table, &bytes), nullptr);
        payload_bytes += bytes;
    }
    const size_t exact_bytes = intent_stage_memory_bytes(reference.get());
    ASSERT_GE(exact_bytes, empty_bytes + payload_bytes);
    Stage bounded(intent_stage_new_bounded(0, exact_bytes), intent_stage_free);
    ASSERT_NE(bounded, nullptr);
    ASSERT_EQ(add_entity(bounded.get()), 0);
    ASSERT_EQ(add_body(bounded.get(), body()), 0);
    ASSERT_EQ(add_attestation(bounded.get()), 0);
    EXPECT_EQ(intent_stage_memory_bytes(bounded.get()), exact_bytes);
    EXPECT_FALSE(intent_stage_allocation_failed(bounded.get()));
    for (const auto table : {INTENT_STAGE_TABLE_ENTITIES,
             INTENT_STAGE_TABLE_PHYSICALITIES, INTENT_STAGE_TABLE_ATTESTATIONS}) {
        size_t expected_size = 0, actual_size = 0;
        const auto* expected = intent_stage_tuple_ptr(reference.get(), table, &expected_size);
        const auto* actual = intent_stage_tuple_ptr(bounded.get(), table, &actual_size);
        ASSERT_EQ(actual_size, expected_size);
        EXPECT_EQ(std::memcmp(actual, expected, actual_size), 0);
    }
    EXPECT_EQ(intent_stage_witness_record(bounded.get(), &kSource), 0);
    EXPECT_TRUE(intent_stage_allocation_failed(bounded.get()));
    EXPECT_EQ(intent_stage_memory_bytes(bounded.get()), exact_bytes);
    EXPECT_EQ(intent_stage_entity_count(bounded.get()), 1u);
    EXPECT_EQ(intent_stage_physicality_count(bounded.get()), 1u);
    EXPECT_EQ(intent_stage_attestation_count(bounded.get()), 1u);
}

TEST(PhysicalityDescriptorStageAllocation, WitnessStorageIsChargedAndExistingWitnessesNeedNoGrowth) {
    Stage probe(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(probe, nullptr);
    const size_t empty_bytes = intent_stage_memory_bytes(probe.get());
    const hash128_t first{1, 1};
    EXPECT_EQ(intent_stage_witness_record(probe.get(), &first), 0);
    ASSERT_TRUE(intent_stage_witness_seen(probe.get(), &first));
    const size_t with_witness = intent_stage_memory_bytes(probe.get());
    ASSERT_GT(with_witness, empty_bytes);
    Stage too_small(intent_stage_new_bounded(0, with_witness - 1u), intent_stage_free);
    ASSERT_NE(too_small, nullptr);
    EXPECT_EQ(intent_stage_witness_record(too_small.get(), &first), 0);
    EXPECT_TRUE(intent_stage_allocation_failed(too_small.get()));
    EXPECT_FALSE(intent_stage_witness_seen(too_small.get(), &first));
    EXPECT_EQ(intent_stage_memory_bytes(too_small.get()), empty_bytes);

    // Discover the occupancy at which another new witness requires growth;
    // the test does not duplicate the owner's load-factor formula.
    Stage capacity_probe(intent_stage_new_bounded(0, with_witness), intent_stage_free);
    ASSERT_NE(capacity_probe, nullptr);
    size_t accepted = 0;
    for (size_t i = 1; i <= 100000u; ++i) {
        hash128_t id{};
        id.lo = i;
        id.hi = 1;
        EXPECT_EQ(intent_stage_witness_record(capacity_probe.get(), &id), 0);
        if (intent_stage_allocation_failed(capacity_probe.get())) break;
        ASSERT_TRUE(intent_stage_witness_seen(capacity_probe.get(), &id));
        ++accepted;
    }
    ASSERT_TRUE(intent_stage_allocation_failed(capacity_probe.get()));
    ASSERT_GT(accepted, 0u);
    Stage exact(intent_stage_new_bounded(0, with_witness), intent_stage_free);
    ASSERT_NE(exact, nullptr);
    for (size_t i = 1; i <= accepted; ++i) {
        hash128_t id{};
        id.lo = i;
        id.hi = 1;
        ASSERT_EQ(intent_stage_witness_record(exact.get(), &id), 0);
        ASSERT_FALSE(intent_stage_allocation_failed(exact.get()));
    }
    EXPECT_EQ(intent_stage_witness_record(exact.get(), &first), 1);
    EXPECT_FALSE(intent_stage_allocation_failed(exact.get()));
    EXPECT_EQ(intent_stage_memory_bytes(exact.get()), with_witness);
}

TEST(PhysicalityDescriptorStageAllocation, BufferGrowthChargesOldAndNewStorageAtTheSameTime) {
    Stage reference(intent_stage_new_bounded(0, 1024u * 1024u), intent_stage_free);
    ASSERT_NE(reference, nullptr);
    size_t rows = 0;
    do {
        ASSERT_EQ(add_entity(reference.get()), 0);
        ++rows;
        ASSERT_LT(rows, 100u);
    } while (intent_stage_memory_peak_bytes(reference.get()) == intent_stage_memory_bytes(reference.get()));
    const size_t retained = intent_stage_memory_bytes(reference.get());
    const size_t peak = intent_stage_memory_peak_bytes(reference.get());
    ASSERT_GT(peak, retained);
    Stage sufficient(intent_stage_new_bounded(0, peak), intent_stage_free);
    ASSERT_NE(sufficient, nullptr);
    for (size_t i = 0; i < rows; ++i) ASSERT_EQ(add_entity(sufficient.get()), 0);
    EXPECT_EQ(intent_stage_memory_bytes(sufficient.get()), retained);
    EXPECT_EQ(intent_stage_memory_peak_bytes(sufficient.get()), peak);
    Stage insufficient(intent_stage_new_bounded(0, retained), intent_stage_free);
    ASSERT_NE(insufficient, nullptr);
    for (size_t i = 0; i < rows; ++i)
        if (add_entity(insufficient.get()) != 0) break;
    EXPECT_TRUE(intent_stage_allocation_failed(insufficient.get()));
    EXPECT_LT(intent_stage_entity_count(insufficient.get()), rows);
    EXPECT_LE(intent_stage_memory_peak_bytes(insufficient.get()), retained);
}

TEST(PhysicalityDescriptorVocabulary, GeneratedSourceIsOrdinarySelfWitnessedContentUnderItsBudget) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    const char* name = physicality_descriptor_generated_source_name();
    ASSERT_NE(name, nullptr);
    ASSERT_GT(std::strlen(name), 0u);
    hash128_t expected_id{};
    ASSERT_EQ(laplace_content_root_id(reinterpret_cast<const uint8_t*>(name),
        std::strlen(name), &expected_id), 0);
    hash128_t actual_id{};
    intent_stage_t* raw = nullptr;
    size_t peak = 0;
    ASSERT_EQ(physicality_descriptor_generated_source_create(kVocabularyBudget,
        &actual_id, &raw, &peak), PHYSICALITY_DESCRIPTOR_OK);
    Stage actual(raw, intent_stage_free);
    ASSERT_NE(actual, nullptr);
    EXPECT_TRUE(hash128_equals(&actual_id, &expected_id));
    EXPECT_GT(intent_stage_entity_count(actual.get()), 0u);
    EXPECT_GT(intent_stage_physicality_count(actual.get()), 0u);
    EXPECT_GE(peak, intent_stage_memory_peak_bytes(actual.get()));
    EXPECT_LE(peak, kVocabularyBudget);
    Stage expected(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(expected, nullptr);
    hash128_t emitted{};
    ASSERT_EQ(content_witness_batch_add(expected.get(), reinterpret_cast<const uint8_t*>(name),
        std::strlen(name), &expected_id, &emitted), 0);
    EXPECT_TRUE(hash128_equals(&emitted, &expected_id));
    hash128_t actual_digest{}, expected_digest{};
    ASSERT_EQ(intent_stage_semantic_digest(actual.get(), &actual_digest), 0);
    ASSERT_EQ(intent_stage_semantic_digest(expected.get(), &expected_digest), 0);
    EXPECT_TRUE(hash128_equals(&actual_digest, &expected_digest));
    for (const size_t budget : {size_t{1}, peak - 1u}) {
        hash128_t untouched = kSource;
        intent_stage_t* failed = nullptr;
        size_t failed_peak = std::numeric_limits<size_t>::max();
        const auto status = physicality_descriptor_generated_source_create(budget,
            &untouched, &failed, &failed_peak);
        Stage cleanup(failed, intent_stage_free);
        if (budget != 1u && status == PHYSICALITY_DESCRIPTOR_OK) {
            // A bounded stage may trim spare buffer capacity under this grant.
            ASSERT_NE(cleanup, nullptr);
            EXPECT_TRUE(hash128_equals(&untouched, &expected_id));
            EXPECT_LE(failed_peak, budget);
            EXPECT_LE(intent_stage_memory_peak_bytes(cleanup.get()), failed_peak);
            hash128_t tighter_digest{};
            ASSERT_EQ(intent_stage_semantic_digest(cleanup.get(), &tighter_digest), 0);
            EXPECT_TRUE(hash128_equals(&tighter_digest, &expected_digest));
        } else {
            EXPECT_EQ(status, PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
            EXPECT_EQ(cleanup, nullptr);
            EXPECT_TRUE(hash128_equals(&untouched, &kSource));
            EXPECT_EQ(failed_peak, 0u);
        }
    }
    raw = nullptr;
    size_t exact_peak = 0;
    ASSERT_EQ(physicality_descriptor_generated_source_create(peak,
        &actual_id, &raw, &exact_peak), PHYSICALITY_DESCRIPTOR_OK);
    Stage exact(raw, intent_stage_free);
    ASSERT_NE(exact, nullptr);
    EXPECT_TRUE(hash128_equals(&actual_id, &expected_id));
    EXPECT_EQ(exact_peak, peak);
}

TEST(PhysicalityDescriptorVocabulary, SessionProjectionHasAnOrdinaryDistinctDerivationSource) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    const char* name = physicality_descriptor_session_source_name();
    ASSERT_STREQ(name, "substrate/source/SessionProjection/v1");
    hash128_t expected{}, actual{}, generated{};
    ASSERT_EQ(laplace_content_root_id(reinterpret_cast<const uint8_t*>(name),
        std::strlen(name), &expected), 0);
    intent_stage_t *raw = nullptr, *other = nullptr;
    size_t peak = 0;
    ASSERT_EQ(physicality_descriptor_session_source_create(kVocabularyBudget,
        &actual, &raw, &peak), PHYSICALITY_DESCRIPTOR_OK);
    Stage stage(raw, intent_stage_free);
    ASSERT_NE(stage, nullptr);
    EXPECT_TRUE(hash128_equals(&actual, &expected));
    EXPECT_GT(intent_stage_entity_count(stage.get()), 0u);
    EXPECT_GT(intent_stage_physicality_count(stage.get()), 0u);
    EXPECT_LE(peak, kVocabularyBudget);
    ASSERT_EQ(physicality_descriptor_generated_source_create(kVocabularyBudget,
        &generated, &other, nullptr), PHYSICALITY_DESCRIPTOR_OK);
    Stage generated_stage(other, intent_stage_free);
    EXPECT_FALSE(hash128_equals(&actual, &generated));
    Stage ordinary(intent_stage_new(0), intent_stage_free);
    hash128_t emitted{}, actual_digest{}, ordinary_digest{};
    ASSERT_EQ(content_witness_batch_add(ordinary.get(), reinterpret_cast<const uint8_t*>(name),
        std::strlen(name), &expected, &emitted), 0);
    ASSERT_EQ(intent_stage_semantic_digest(stage.get(), &actual_digest), 0);
    ASSERT_EQ(intent_stage_semantic_digest(ordinary.get(), &ordinary_digest), 0);
    EXPECT_TRUE(hash128_equals(&actual_digest, &ordinary_digest));
    raw = nullptr; actual = kSource; peak = 123;
    EXPECT_EQ(physicality_descriptor_session_source_create(1, &actual, &raw, &peak),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(raw, nullptr);
    EXPECT_TRUE(hash128_equals(&actual, &kSource));
    EXPECT_EQ(peak, 0u);
}

TEST(PhysicalityDescriptorVocabulary, AllNumericAndSchemaRootsMatchTheOrdinaryContentOwner) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    auto provider = vocabulary();
    ASSERT_NE(provider, nullptr);
    const auto* basis = physicality_descriptor_vocabulary_basis(provider.get());
    ASSERT_NE(basis, nullptr);
    EXPECT_TRUE(physicality_descriptor_basis_is_valid(basis));
    for (size_t i = 0; i < kTags.size(); ++i) {
        SCOPED_TRACE(kTags[i]);
        hash128_t expected{};
        ASSERT_EQ(laplace_content_root_id(reinterpret_cast<const uint8_t*>(kTags[i]),
            std::strlen(kTags[i]), &expected), 0);
        EXPECT_TRUE(hash128_equals(&expected, &basis->tags[i]));
    }
    for (uint32_t value = 0; value < 256u; ++value) {
        SCOPED_TRACE(value);
        const std::string text = std::to_string(value);
        hash128_t expected{};
        ASSERT_EQ(laplace_content_root_id(reinterpret_cast<const uint8_t*>(text.data()),
            text.size(), &expected), 0);
        EXPECT_TRUE(hash128_equals(&expected, &basis->byte_numbers[value]));
        if (value < 10u) {
            const auto* digit = codepoint_table_lookup('0' + value);
            ASSERT_NE(digit, nullptr);
            EXPECT_TRUE(hash128_equals(&digit->hash, &basis->byte_numbers[value]));
        }
    }
}

TEST(PhysicalityDescriptorVocabulary, GeneratedStageMatchesOrdinaryWitnessEmission) {
    auto provider = vocabulary();
    ASSERT_NE(provider, nullptr);
    Stage actual(physicality_descriptor_vocabulary_take_stage(provider.get()), intent_stage_free);
    Stage expected(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(actual, nullptr);
    ASSERT_NE(expected, nullptr);
    const auto emit = [&](const std::string& text) {
        hash128_t root{};
        EXPECT_EQ(content_witness_batch_add(expected.get(),
            reinterpret_cast<const uint8_t*>(text.data()), text.size(), &kSource, &root), 0);
    };
    for (const auto* tag : kTags) emit(tag);
    for (uint32_t value = 0; value < 256u; ++value) emit(std::to_string(value));
    for (const auto* tag : {"PhysicalityViewV1", "PhysicalityCurrentFloorAdmittedWinnerRecipeV1",
             "PhysicalityFloorReceiptV1", "PhysicalityReferenceSelectionV1",
             "PhysicalitySelectionScopeV1", "PhysicalitySourceUnitContextV1",
             "PhysicalitySourceIdentifierV1", "PhysicalitySourceUnitReceiptV1"}) emit(tag);
    hash128_t expected_digest{}, actual_digest{};
    ASSERT_EQ(intent_stage_semantic_digest(expected.get(), &expected_digest), 0);
    ASSERT_EQ(intent_stage_semantic_digest(actual.get(), &actual_digest), 0);
    EXPECT_TRUE(hash128_equals(&actual_digest, &expected_digest));
    EXPECT_EQ(intent_stage_entity_count(actual.get()), intent_stage_entity_count(expected.get()));
    EXPECT_EQ(intent_stage_physicality_count(actual.get()), intent_stage_physicality_count(expected.get()));
    EXPECT_EQ(intent_stage_attestation_count(actual.get()), intent_stage_attestation_count(expected.get()));
}

TEST(PhysicalityDescriptorVocabulary, ReceiptIsCopiedAndStageOwnershipTransfersExactlyOnce) {
    struct RestoreFloor {
        ~RestoreFloor() { EXPECT_EQ(codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS), 0); }
    } restore;
    hash128_t expected_receipt{};
    ASSERT_EQ(codepoint_table_copy_receipt(&expected_receipt), 0);
    auto provider = vocabulary();
    ASSERT_NE(provider, nullptr);
    const auto* receipt = physicality_descriptor_vocabulary_floor_receipt(provider.get());
    ASSERT_NE(receipt, nullptr);
    Stage stage(physicality_descriptor_vocabulary_take_stage(provider.get()), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    EXPECT_EQ(physicality_descriptor_vocabulary_take_stage(provider.get()), nullptr);
    hash128_t before{};
    ASSERT_EQ(intent_stage_semantic_digest(stage.get(), &before), 0);
    codepoint_table_unload();
    EXPECT_TRUE(hash128_equals(receipt, &expected_receipt));
    EXPECT_TRUE(physicality_descriptor_basis_is_valid(
        physicality_descriptor_vocabulary_basis(provider.get())));
    provider.reset();
    hash128_t after{};
    ASSERT_EQ(intent_stage_semantic_digest(stage.get(), &after), 0);
    EXPECT_TRUE(hash128_equals(&before, &after));
    EXPECT_GT(intent_stage_entity_count(stage.get()), 0u);
    EXPECT_GT(intent_stage_physicality_count(stage.get()), 0u);
}

TEST(PhysicalityDescriptorVocabulary, RejectsMissingFloorAndInsufficientRetainedBudgetWithoutOutput) {
    ASSERT_TRUE(codepoint_table_is_loaded());
    physicality_descriptor_vocabulary_t* output = nullptr;
    EXPECT_EQ(physicality_descriptor_vocabulary_create(nullptr, kVocabularyBudget, &output),
        PHYSICALITY_DESCRIPTOR_INVALID);
    EXPECT_EQ(output, nullptr);
    for (const size_t budget : {size_t{0}, size_t{64u * 1024u}}) {
        EXPECT_EQ(physicality_descriptor_vocabulary_create(&kSource, budget, &output),
            PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
        EXPECT_EQ(output, nullptr);
    }
    struct RestoreFloor {
        ~RestoreFloor() { EXPECT_EQ(codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS), 0); }
    } restore;
    codepoint_table_unload();
    EXPECT_EQ(physicality_descriptor_vocabulary_create(&kSource, kVocabularyBudget, &output),
        PHYSICALITY_DESCRIPTOR_MISSING_FLOOR);
    EXPECT_EQ(output, nullptr);
}

TEST(PhysicalityDescriptorVocabulary, FloorIndexPreparationIsBoundedReportedAndReused) {
    struct RestoreFloor {
        ~RestoreFloor() { EXPECT_EQ(codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS), 0); }
    } restore;
    ASSERT_EQ(codepoint_table_load_perfcache(LAPLACE_PERFCACHE_PATH_FOR_TESTS), 0);
    ASSERT_FALSE(codepoint_table_id_index_ready());
    uint64_t floor_count = 0;
    ASSERT_EQ(codepoint_table_records(nullptr, &floor_count), 0);
    const size_t index_bytes = static_cast<size_t>(floor_count) * sizeof(uint32_t);
    ASSERT_GT(index_bytes, 0u);
    size_t added = 99;
    EXPECT_EQ(codepoint_table_prepare_id_index(index_bytes - 1u, &added), -2);
    EXPECT_EQ(added, 0u);
    EXPECT_FALSE(codepoint_table_id_index_ready());
    EXPECT_EQ(codepoint_table_id_index_bytes(), 0u);
    auto first = vocabulary();
    ASSERT_NE(first, nullptr);
    EXPECT_TRUE(codepoint_table_id_index_ready());
    EXPECT_EQ(codepoint_table_id_index_bytes(), index_bytes);
    EXPECT_EQ(physicality_descriptor_vocabulary_floor_index_added_bytes(first.get()), index_bytes);
    EXPECT_LE(physicality_descriptor_vocabulary_peak_bytes(first.get()), kVocabularyBudget);
    auto second = vocabulary();
    ASSERT_NE(second, nullptr);
    EXPECT_EQ(physicality_descriptor_vocabulary_floor_index_added_bytes(second.get()), 0u);
    const size_t retained = physicality_descriptor_vocabulary_bytes(first.get());
    Stage transferred(physicality_descriptor_vocabulary_take_stage(first.get()), intent_stage_free);
    ASSERT_NE(transferred, nullptr);
    EXPECT_EQ(retained - physicality_descriptor_vocabulary_bytes(first.get()),
        intent_stage_memory_bytes(transferred.get()));
    first.reset();
    second.reset();
    EXPECT_TRUE(codepoint_table_id_index_ready());
    EXPECT_EQ(codepoint_table_id_index_bytes(), index_bytes);
    added = 99;
    EXPECT_EQ(codepoint_table_prepare_id_index(0, &added), 0);
    EXPECT_EQ(added, 0u);
}

TEST(PhysicalityDescriptorProviderBatch, PreservesEveryBodyVariantAndObservationInTheNativeStage) {
    auto provider = vocabulary();
    ASSERT_NE(provider, nullptr);
    auto alternate = body();
    alternate.coord[0] = -0.0;
    const std::array<physicality_descriptor_input_t, 3> inputs{body(), alternate, body()};
    const std::array<int64_t, 3> times{-1234567, 1700000000000000, 1700000000000001};
    Stage stage(intent_stage_new_bounded(0, 1024u * 1024u), intent_stage_free);
    ASSERT_NE(stage, nullptr);
    ASSERT_EQ(physicality_descriptor_stage_add_batch(stage.get(), inputs.data(), nullptr, times.data(), inputs.size()),
        PHYSICALITY_DESCRIPTOR_OK);
    ASSERT_EQ(intent_stage_physicality_count(stage.get()), inputs.size());
    const physicality_descriptor_limits_t limits{8u * 1024u * 1024u};
    const std::array<const intent_stage_t*, 1> stages{stage.get()};
    physicality_descriptor_capture_t* raw = nullptr;
    ASSERT_EQ(physicality_descriptor_capture_stages(stages.data(), stages.size(),
        physicality_descriptor_vocabulary_basis(provider.get()), &limits,
        16u * 1024u * 1024u, &raw), PHYSICALITY_DESCRIPTOR_OK);
    Capture captured(raw, physicality_descriptor_capture_free);
    size_t count = 0;
    const auto* got = physicality_descriptor_capture_inputs(captured.get(), &count);
    ASSERT_EQ(count, inputs.size());
    const auto* observations = physicality_descriptor_capture_observations(captured.get(), nullptr);
    const auto* roots = physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(captured.get()), nullptr);
    EXPECT_TRUE(hash128_equals(&roots[0], &roots[2]));
    EXPECT_FALSE(hash128_equals(&roots[0], &roots[1]));
    hash128_t expected_placement{};
    laplace_physicality_id_compute(inputs[0].entity_id, inputs[0].type, &expected_placement);
    for (size_t i = 0; i < count; ++i) {
        EXPECT_EQ(std::memcmp(got[i].coord, inputs[i].coord, sizeof(inputs[i].coord)), 0);
        EXPECT_TRUE(hash128_equals(&observations[i].placement_id, &expected_placement));
        EXPECT_EQ(observations[i].source_stage_index, 0u);
        EXPECT_EQ(observations[i].source_row_index, i);
        EXPECT_EQ(observations[i].observed_at_unix_us, times[i]);
    }
}

TEST(PhysicalityDescriptorProviderBatch, ReportsResourceFailureAndKeepsPriorSuccessfulRowsOnInvalidInput) {
    Stage probe(intent_stage_new(0), intent_stage_free);
    ASSERT_NE(probe, nullptr);
    Stage exhausted(intent_stage_new_bounded(0, intent_stage_memory_bytes(probe.get())), intent_stage_free);
    ASSERT_NE(exhausted, nullptr);
    const auto good = body();
    const int64_t time = 0;
    EXPECT_EQ(physicality_descriptor_stage_add_batch(exhausted.get(), &good, nullptr, &time, 1),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_TRUE(intent_stage_allocation_failed(exhausted.get()));
    auto invalid = good;
    invalid.n_constituents = -1;
    const std::array<physicality_descriptor_input_t, 2> inputs{good, invalid};
    const std::array<int64_t, 2> times{0, 1};
    EXPECT_EQ(physicality_descriptor_stage_add_batch(probe.get(), inputs.data(), nullptr, times.data(), inputs.size()),
        PHYSICALITY_DESCRIPTOR_INVALID_BODY);
    EXPECT_EQ(intent_stage_physicality_count(probe.get()), 1u);
    EXPECT_FALSE(intent_stage_allocation_failed(probe.get()));
    EXPECT_EQ(physicality_descriptor_stage_add_batch(probe.get(), nullptr, nullptr, nullptr, 0),
        PHYSICALITY_DESCRIPTOR_OK);
    EXPECT_EQ(physicality_descriptor_stage_add_batch(probe.get(), nullptr, nullptr, &time, 1),
        PHYSICALITY_DESCRIPTOR_INVALID);
    auto overflow = good;
    overflow.trajectory_vertices = std::numeric_limits<size_t>::max();
    EXPECT_EQ(physicality_descriptor_stage_add_batch(probe.get(), &overflow, nullptr, &time, 1),
        PHYSICALITY_DESCRIPTOR_RESOURCE_EXHAUSTED);
    EXPECT_EQ(intent_stage_physicality_count(probe.get()), 1u);
}

} // namespace
