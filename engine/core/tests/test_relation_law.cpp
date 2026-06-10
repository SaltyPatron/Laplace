#include <gtest/gtest.h>

#include <cstdio>
#include <cstring>

#include "laplace/core/attestation_engine.h"
#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"

namespace {

hash128_t hash_path(const char* path) {
    hash128_t h;
    hash128_blake3(reinterpret_cast<const uint8_t*>(path), std::strlen(path), &h);
    return h;
}

hash128_t type_id(const char* name) {
    char path[128];
    std::snprintf(path, sizeof(path), "substrate/type/%s/v1", name);
    return hash_path(path);
}

}  // namespace

TEST(LaplaceRelationLaw, HasUposResolvesToHasPos) {
    hash128_t upos, pos;
    ASSERT_EQ(0, laplace_relation_resolve("HAS_UPOS", &upos));
    ASSERT_EQ(0, laplace_relation_resolve("HAS_POS", &pos));
    EXPECT_TRUE(hash128_equals(&upos, &pos));
}

TEST(LaplaceRelationLaw, HasXposInHasPosFamily) {
    hash128_t xpos;
    ASSERT_EQ(0, laplace_relation_resolve("HAS_XPOS", &xpos));
    int in_family = 0;
    ASSERT_EQ(0, laplace_relation_in_family(&xpos, "HAS_POS", &in_family));
    EXPECT_EQ(1, in_family);
}

TEST(LaplaceAttestationEngine, SymmetricTranslationOneId) {
    hash128_t src = hash_path("substrate/test/reg/source");
    hash128_t a   = hash_path("substrate/test/reg/a");
    hash128_t b   = hash_path("substrate/test/reg/b");

    laplace_attestation_staged_t ab, ba;
    ASSERT_EQ(0, laplace_attestation_categorical_build(
        "IS_TRANSLATION_OF", &a, &b, 0, &src, NULL, 1, 0.70, 1, 1, 0, &ab));
    ASSERT_EQ(0, laplace_attestation_categorical_build(
        "IS_TRANSLATION_OF", &b, &a, 0, &src, NULL, 1, 0.70, 1, 1, 0, &ba));

    EXPECT_TRUE(hash128_equals(&ab.id, &ba.id));
    EXPECT_TRUE(hash128_equals(&ab.subject_id, &ba.subject_id));
    EXPECT_TRUE(hash128_equals(&ab.object_id, &ba.object_id));
}

TEST(LaplaceAttestationEngine, FlipHypernymCollapsesArena) {
    hash128_t src    = hash_path("src");
    hash128_t animal = hash_path("e/animal");
    hash128_t dog    = hash_path("e/dog");

    laplace_attestation_staged_t flipped, direct;
    ASSERT_EQ(0, laplace_attestation_categorical_build(
        "HAS_HYPONYM", &animal, &dog, 0, &src, NULL, 1, 1.0, 1, 1, 0, &flipped));
    ASSERT_EQ(0, laplace_attestation_categorical_build(
        "IS_A", &dog, &animal, 0, &src, NULL, 1, 1.0, 1, 1, 0, &direct));

    EXPECT_TRUE(hash128_equals(&flipped.id, &direct.id));
    EXPECT_TRUE(hash128_equals(&flipped.subject_id, &dog));
    EXPECT_TRUE(hash128_equals(&flipped.object_id, &animal));
}

TEST(LaplacePosLaw, UposCanonicalRoundTrip) {
    hash128_t id;
    ASSERT_EQ(0, laplace_pos_resolve_entity("NOUN", LAPLACE_POS_TAGSET_UPOS, &id));
    hash128_t expected = hash_path("substrate/pos/NOUN/v1");
    EXPECT_TRUE(hash128_equals(&id, &expected));
}

TEST(LaplaceRelationLaw, DeprelDynamicFamily) {
    hash128_t tid, parent_id;
    double rank = 0;
    laplace_rel_symmetry_t sym = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    uint8_t flip = 1;
    ASSERT_EQ(0, laplace_relation_resolve_deprel(
        "nsubj", &tid, &rank, &sym, &flip, &parent_id));
    EXPECT_TRUE(hash128_equals(&type_id("DEP_NSUBJ"), &tid));
    EXPECT_TRUE(hash128_equals(&type_id("DEPENDS_ON"), &parent_id));
    EXPECT_DOUBLE_EQ(0.73, rank);
    EXPECT_EQ(0, flip);

    ASSERT_EQ(0, laplace_relation_resolve_deprel(
        "nsubj:pass", &tid, &rank, &sym, &flip, &parent_id));
    EXPECT_TRUE(hash128_equals(&type_id("DEP_NSUBJ_PASS"), &tid));
    EXPECT_TRUE(hash128_equals(&type_id("DEP_NSUBJ"), &parent_id));
}

TEST(LaplaceRelationLaw, FeatureDynamicFamily) {
    hash128_t tid, parent_id;
    double rank = 0;
    laplace_rel_symmetry_t sym = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    uint8_t flip = 1;
    ASSERT_EQ(0, laplace_relation_resolve_feature(
        "Number", &tid, &rank, &sym, &flip, &parent_id));
    EXPECT_TRUE(hash128_equals(&type_id("FEAT_NUMBER"), &tid));
    EXPECT_TRUE(hash128_equals(&type_id("HAS_FEATURE"), &parent_id));
}

TEST(LaplacePosLaw, WiktionaryMapsToCanonical) {
    hash128_t id;
    ASSERT_EQ(0, laplace_pos_resolve_entity("noun", LAPLACE_POS_TAGSET_WIKTIONARY, &id));
    hash128_t expected = hash_path("substrate/pos/NOUN/v1");
    EXPECT_TRUE(hash128_equals(&id, &expected));
}

TEST(LaplaceAttestationEngine, ResolvedScored_PositiveAboveHalf) {
    hash128_t subj = hash_path("s");
    hash128_t type = type_id("REL");
    hash128_t obj  = hash_path("o");
    hash128_t src  = hash_path("src");

    laplace_attestation_staged_t staged;
    ASSERT_EQ(0, laplace_attestation_resolved_scored_build(
        &subj, &type, &obj, 0, &src, NULL, 1, 0.5, 1.5, 1.0, 1, 0, &staged));
    EXPECT_GT(staged.score_fp1e9, LAPLACE_GLICKO2_FP_SCALE / 2);
    EXPECT_EQ(LAPLACE_ATTESTATION_OUTCOME_CONFIRM, staged.outcome);
    EXPECT_EQ(0, staged.is_aggregated);
}

TEST(LaplaceAttestationEngine, ResolvedScored_NegativeBelowHalf) {
    hash128_t subj = hash_path("s");
    hash128_t type = type_id("REL");
    hash128_t obj  = hash_path("o");
    hash128_t src  = hash_path("src");

    laplace_attestation_staged_t staged;
    ASSERT_EQ(0, laplace_attestation_resolved_scored_build(
        &subj, &type, &obj, 0, &src, NULL, 1, 0.5, -1.5, 1.0, 1, 0, &staged));
    EXPECT_LT(staged.score_fp1e9, LAPLACE_GLICKO2_FP_SCALE / 2);
    EXPECT_EQ(LAPLACE_ATTESTATION_OUTCOME_REFUTE, staged.outcome);
}

TEST(LaplaceAttestationEngine, WitnessPhi_TrustedTighterThanCrank) {
    EXPECT_LT(laplace_attestation_witness_phi(1.0), laplace_attestation_witness_phi(0.1));
}

TEST(LaplaceAttestationEngine, Aggregated_OutcomeFromNetScore) {
    hash128_t subj = hash_path("s");
    hash128_t type = type_id("PRECEDES");
    hash128_t obj  = hash_path("o");
    hash128_t src  = hash_path("src");

    laplace_attestation_staged_t win, loss, draw;
    ASSERT_EQ(0, laplace_attestation_aggregated_build(
        &subj, &type, &obj, 0, &src, NULL, 1, 1.0, 2,
        2LL * LAPLACE_GLICKO2_FP_SCALE, 0, &win));
    ASSERT_EQ(0, laplace_attestation_aggregated_build(
        &subj, &type, &obj, 0, &src, NULL, 1, 1.0, 2, 0, 0, &loss));
    ASSERT_EQ(0, laplace_attestation_aggregated_build(
        &subj, &type, &obj, 0, &src, NULL, 1, 1.0, 2,
        LAPLACE_GLICKO2_FP_SCALE, 0, &draw));

    EXPECT_EQ(LAPLACE_ATTESTATION_OUTCOME_CONFIRM, win.outcome);
    EXPECT_EQ(LAPLACE_ATTESTATION_OUTCOME_REFUTE, loss.outcome);
    EXPECT_EQ(LAPLACE_ATTESTATION_OUTCOME_DRAW, draw.outcome);
    EXPECT_EQ(1, win.is_aggregated);
    EXPECT_EQ(2LL * LAPLACE_GLICKO2_FP_SCALE, win.sum_score_fp1e9);
    EXPECT_EQ(LAPLACE_GLICKO2_FP_SCALE, win.score_fp1e9);
    EXPECT_TRUE(hash128_equals(&win.id, &loss.id));
}
