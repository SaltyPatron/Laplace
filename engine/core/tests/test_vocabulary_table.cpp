#include <gtest/gtest.h>

#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <memory>
#include <string>
#include <vector>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/deprel_law.h"
#include "laplace/core/hash128.h"
#include "laplace/core/ordered_composition.h"
#include "laplace/core/pos_law.h"
#include "laplace/core/tier_tree.h"
#include "laplace/core/vocabulary_table.h"

#ifndef LAPLACE_VOCABULARY_PERFCACHE_PATH_FOR_TESTS
#error "LAPLACE_VOCABULARY_PERFCACHE_PATH_FOR_TESTS must be defined"
#endif

namespace {

class VocabularyTableEnv : public ::testing::Environment {
public:
    void SetUp() override {
        if (vocabulary_table_is_loaded()) return;
        ASSERT_EQ(0, vocabulary_table_load(LAPLACE_VOCABULARY_PERFCACHE_PATH_FOR_TESTS));
    }
};

::testing::Environment* const g_vt_env = ::testing::AddGlobalTestEnvironment(new VocabularyTableEnv);

// The ordinary content path a recipe takes for a value's text.
tier_node_view_t content_root(const char* text) {
    tier_tree_t* raw = nullptr;
    EXPECT_EQ(0, content_witness_source_tree_build(reinterpret_cast<const uint8_t*>(text), std::strlen(text), &raw));
    std::unique_ptr<tier_tree_t, decltype(&tier_tree_free)> tree(raw, tier_tree_free);
    tier_node_view_t root{};
    EXPECT_EQ(0, content_witness_tree_root_node(tree.get(), &root));
    return root;
}

}  // namespace

TEST(VocabularyTable, FamiliesMatchTheCompiledLaw) {
    (void)g_vt_env;
    ASSERT_TRUE(codepoint_table_is_loaded());
    size_t upos = 0;
    laplace_pos_upos_canonical(&upos);
    EXPECT_EQ(upos, vocabulary_table_family_count(LAPLACE_VOCABULARY_UPOS));
    EXPECT_EQ(uint32_t(laplace_deprel_count()), vocabulary_table_family_count(LAPLACE_VOCABULARY_DEPREL));
    EXPECT_EQ(uint32_t(laplace_deprel_subtype_count()),
              vocabulary_table_family_count(LAPLACE_VOCABULARY_DEPREL_SUBTYPE));
    EXPECT_GT(vocabulary_table_family_count(LAPLACE_VOCABULARY_FEATURE), 0u);
    EXPECT_GT(vocabulary_table_family_count(LAPLACE_VOCABULARY_FEATURE_VALUE), 0u);
}

TEST(VocabularyTable, CodeAndContentIdResolveEachOther) {
    const char* canonical = nullptr;
    int index = -1;
    ASSERT_EQ(0, laplace_pos_resolve_canonical("NOUN", LAPLACE_POS_TAGSET_UPOS, &canonical, &index));
    const auto* noun = vocabulary_table_lookup(LAPLACE_VOCABULARY_UPOS, uint16_t(index + 1));
    ASSERT_NE(nullptr, noun);
    EXPECT_STREQ("NOUN", vocabulary_table_label(noun));

    hash128_t label_id{};
    ASSERT_EQ(0, laplace_pos_resolve_entity("NOUN", LAPLACE_POS_TAGSET_UPOS, &label_id));
    EXPECT_TRUE(hash128_equals(&label_id, &noun->id));
    EXPECT_EQ(noun, vocabulary_table_find(LAPLACE_VOCABULARY_UPOS, &label_id));

    const tier_node_view_t root = content_root("NOUN");
    EXPECT_TRUE(hash128_equals(&root.id, &noun->id));
    EXPECT_EQ(0, std::memcmp(root.coord, noun->coord, sizeof(root.coord)));
    EXPECT_EQ(0, std::memcmp(&root.hilbert, &noun->hilbert, sizeof(root.hilbert)));
    EXPECT_EQ(root.tier, noun->tier);

    const tier_node_view_t nsubj = content_root("nsubj");
    const auto* rel = vocabulary_table_find(LAPLACE_VOCABULARY_DEPREL, &nsubj.id);
    ASSERT_NE(nullptr, rel);
    EXPECT_EQ(LAPLACE_VOCABULARY_DEPREL, rel->family);
    EXPECT_EQ(laplace_deprel_code("nsubj:pass"), rel->code);

    const tier_node_view_t pass = content_root("pass");
    const auto* sub = vocabulary_table_find(LAPLACE_VOCABULARY_DEPREL_SUBTYPE, &pass.id);
    ASSERT_NE(nullptr, sub);
    EXPECT_EQ(LAPLACE_VOCABULARY_DEPREL_SUBTYPE, sub->family);
    EXPECT_EQ(laplace_deprel_subtype_code("nsubj:pass"), sub->code);
}

TEST(VocabularyTable, FeatureValueIsTheOrderedComposition) {
    const tier_node_view_t number = content_root("Number"), plur = content_root("Plur");
    const auto* feature = vocabulary_table_find(LAPLACE_VOCABULARY_FEATURE, &number.id);
    ASSERT_NE(nullptr, feature);
    EXPECT_EQ(LAPLACE_VOCABULARY_FEATURE, feature->family);

    laplace_ordered_component_t parts[2]{};
    const tier_node_view_t* in[2] = {&number, &plur};
    for (int i = 0; i < 2; ++i) {
        parts[i].id = in[i]->id;
        std::memcpy(parts[i].coord, in[i]->coord, sizeof(parts[i].coord));
        parts[i].tier = in[i]->tier;
        parts[i].atom = in[i]->atom;
        parts[i].has_atom = in[i]->tier == 0;
    }
    laplace_ordered_composition_request_t request{};
    request.components = parts;
    request.component_count = 2;
    laplace_ordered_composition_result_t pair{};
    ASSERT_EQ(0, laplace_ordered_composition_compose_batch(&request, 1, &pair));

    const auto* value = vocabulary_table_find(LAPLACE_VOCABULARY_FEATURE_VALUE, &pair.id);
    ASSERT_NE(nullptr, value);
    EXPECT_EQ(LAPLACE_VOCABULARY_FEATURE_VALUE, value->family);
    EXPECT_STREQ("Number=Plur", vocabulary_table_label(value));
    EXPECT_EQ(feature->code, value->parent_code);
    EXPECT_EQ(0, std::memcmp(pair.coord, value->coord, sizeof(pair.coord)));
    EXPECT_EQ(value, vocabulary_table_lookup(LAPLACE_VOCABULARY_FEATURE_VALUE, value->code));
}

TEST(VocabularyTable, MissesAreNotAuthoritative) {
    EXPECT_EQ(nullptr, vocabulary_table_lookup(LAPLACE_VOCABULARY_UPOS, 0));
    EXPECT_EQ(nullptr, vocabulary_table_lookup(
        LAPLACE_VOCABULARY_UPOS, uint16_t(vocabulary_table_family_count(LAPLACE_VOCABULARY_UPOS) + 1)));
    const tier_node_view_t cat = content_root("cat");
    EXPECT_EQ(nullptr, vocabulary_table_find(LAPLACE_VOCABULARY_UPOS, &cat.id));
    const tier_node_view_t nsubj = content_root("nsubj");
    EXPECT_EQ(nullptr, vocabulary_table_find(LAPLACE_VOCABULARY_UPOS, &nsubj.id));
}

TEST(VocabularyTable, OneEntityServesSeveralVocabularies) {
    const tier_node_view_t advcl = content_root("advcl");
    const auto* rel = vocabulary_table_find(LAPLACE_VOCABULARY_DEPREL, &advcl.id);
    const auto* sub = vocabulary_table_find(LAPLACE_VOCABULARY_DEPREL_SUBTYPE, &advcl.id);
    ASSERT_NE(nullptr, rel);
    ASSERT_NE(nullptr, sub);
    EXPECT_TRUE(hash128_equals(&rel->id, &sub->id));
    EXPECT_EQ(laplace_deprel_code("advcl"), rel->code);
    EXPECT_EQ(laplace_deprel_subtype_code("acl:advcl"), sub->code);
}

TEST(VocabularyTable, RejectsACorruptedBlobAndKeepsTheLoadedOne) {
    std::ifstream in(LAPLACE_VOCABULARY_PERFCACHE_PATH_FOR_TESTS, std::ios::binary);
    std::vector<char> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    ASSERT_GT(bytes.size(), 200u);
    bytes[150] ^= 0x01;  // inside the first record
    const auto path = std::filesystem::temp_directory_path() / "laplace_vocabulary_corrupt.bin";
    {
        std::ofstream out(path, std::ios::binary);
        out.write(bytes.data(), std::streamsize(bytes.size()));
    }
    EXPECT_EQ(-4, vocabulary_table_load(path.string().c_str()));
    std::filesystem::remove(path);
    EXPECT_TRUE(vocabulary_table_is_loaded());
    EXPECT_EQ(-1, vocabulary_table_load("/nonexistent/vocabulary.bin"));
}
