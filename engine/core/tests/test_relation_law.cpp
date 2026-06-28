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

}  

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
    hash128_t dep_nsubj  = type_id("DEP_NSUBJ");
    hash128_t depends_on = type_id("DEPENDS_ON");
    EXPECT_TRUE(hash128_equals(&dep_nsubj, &tid));
    EXPECT_TRUE(hash128_equals(&depends_on, &parent_id));
    // Deprels sit at the grammatical-glue floor: relation_types.toml [dynamic.deprel] rank =
    // "lexical_glue" (0.18). (Was 0.73/partitive before the salience recalibration; the parent
    // DEPENDS_ON keeps partitive, checked by id above — the deprel's OWN rank is lexical_glue.)
    EXPECT_DOUBLE_EQ(0.18, rank);
    EXPECT_EQ(0, flip);

    ASSERT_EQ(0, laplace_relation_resolve_deprel(
        "nsubj:pass", &tid, &rank, &sym, &flip, &parent_id));
    hash128_t dep_nsubj_pass = type_id("DEP_NSUBJ_PASS");
    EXPECT_TRUE(hash128_equals(&dep_nsubj_pass, &tid));
    EXPECT_TRUE(hash128_equals(&dep_nsubj, &parent_id));
}

TEST(LaplaceRelationLaw, FeatureDynamicFamily) {
    hash128_t tid, parent_id;
    double rank = 0;
    laplace_rel_symmetry_t sym = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
    uint8_t flip = 1;
    ASSERT_EQ(0, laplace_relation_resolve_feature(
        "Number", &tid, &rank, &sym, &flip, &parent_id));
    hash128_t feat_number = type_id("FEAT_NUMBER");
    hash128_t has_feature = type_id("HAS_FEATURE");
    EXPECT_TRUE(hash128_equals(&feat_number, &tid));
    EXPECT_TRUE(hash128_equals(&has_feature, &parent_id));
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

TEST(LaplaceAttestationEngine, AggregatedBatch_IdenticalToPerCell) {
    hash128_t type = type_id("PRECEDES");
    hash128_t sym  = type_id("IS_SYNONYM_OF");
    hash128_t src  = hash_path("src");

    laplace_attestation_aggregated_cell_t cells[4];
    for (int i = 0; i < 4; ++i) {
        char sb[8], ob[8];
        snprintf(sb, sizeof(sb), "s%d", i);
        snprintf(ob, sizeof(ob), "o%d", i);
        cells[i].subject = hash_path(sb);
        cells[i].object  = hash_path(ob);
        cells[i].object_is_null = 0;
        cells[i].games = i + 1;
        cells[i].sum_score_fp1e9 = (i % 2 == 0)
            ? (int64_t)(i + 1) * LAPLACE_GLICKO2_FP_SCALE   
            : 0;                                            
    }

    laplace_attestation_staged_t batch[4];
    ASSERT_EQ(0, laplace_attestation_aggregated_batch_build(
        cells, 4, &type, &src, NULL, 1, 0.7, 12345, batch));

    for (int i = 0; i < 4; ++i) {
        laplace_attestation_staged_t one;
        ASSERT_EQ(0, laplace_attestation_aggregated_build(
            &cells[i].subject, &type, &cells[i].object, 0, &src, NULL, 1,
            0.7, cells[i].games, cells[i].sum_score_fp1e9, 12345, &one));
        EXPECT_TRUE(hash128_equals(&batch[i].id, &one.id)) << "cell " << i;
        EXPECT_EQ(one.outcome, batch[i].outcome) << "cell " << i;
        EXPECT_EQ(one.score_fp1e9, batch[i].score_fp1e9) << "cell " << i;
        EXPECT_EQ(one.sum_score_fp1e9, batch[i].sum_score_fp1e9) << "cell " << i;
        EXPECT_EQ(one.opponent_rd_fp1e9, batch[i].opponent_rd_fp1e9) << "cell " << i;
        EXPECT_EQ(one.observation_count, batch[i].observation_count) << "cell " << i;
        EXPECT_EQ(one.is_aggregated, batch[i].is_aggregated) << "cell " << i;
        EXPECT_TRUE(hash128_equals(&batch[i].subject_id, &one.subject_id)) << "cell " << i;
        EXPECT_TRUE(hash128_equals(&batch[i].object_id, &one.object_id)) << "cell " << i;
    }

    
    laplace_attestation_aggregated_cell_t flipped[2];
    flipped[0].subject = hash_path("zz");
    flipped[0].object  = hash_path("aa");
    flipped[0].object_is_null = 0;
    flipped[0].games = 1;
    flipped[0].sum_score_fp1e9 = LAPLACE_GLICKO2_FP_SCALE;
    flipped[1] = flipped[0];
    flipped[1].subject = hash_path("aa");
    flipped[1].object  = hash_path("zz");

    laplace_attestation_staged_t symed[2];
    ASSERT_EQ(0, laplace_attestation_aggregated_batch_build(
        flipped, 2, &sym, &src, NULL, 1, 0.7, 12345, symed));
    EXPECT_TRUE(hash128_equals(&symed[0].id, &symed[1].id));
}

// ── Reverse-index (laplace_relation_lookup) ──────────────────────────────────────────────────
// The O(1) open-addressing bucket replaced the O(n) linear scan. These prove it is a faithful
// drop-in: every canonical relation resolves back to its own def, misses are rejected, and the
// def fields it returns are intact. (Isolate + prove, before the chain test below.)

TEST(LaplaceRelationLaw, ReverseLookupFindsEveryEntry) {
    // Enumerate via the exported manifest functions (the data table itself is not dll-exported).
    size_t n = laplace_relation_manifest_count();
    ASSERT_GT(n, 0u);
    for (size_t i = 0; i < n; ++i) {
        const char* name = laplace_relation_manifest_canonical(i);
        ASSERT_NE(nullptr, name);
        hash128_t tid = type_id(name);
        const laplace_relation_def_t* def = nullptr;
        ASSERT_EQ(0, laplace_relation_lookup(&tid, &def)) << "miss for " << name;
        ASSERT_NE(nullptr, def);
        // The bucket may land on a different probe slot, but the returned def MUST be the one whose
        // id actually equals the query — i.e. its canonical name round-trips.
        EXPECT_STREQ(name, def->canonical) << "wrong def for " << name;
    }
}

TEST(LaplaceRelationLaw, ReverseLookupMissReturnsError) {
    hash128_t bogus = type_id("NOT_A_REAL_RELATION_ZZZ");
    const laplace_relation_def_t* def = reinterpret_cast<const laplace_relation_def_t*>(0x1);
    EXPECT_EQ(-1, laplace_relation_lookup(&bogus, &def));
}

TEST(LaplaceRelationLaw, ReverseLookupRejectsNullArgs) {
    const laplace_relation_def_t* def = nullptr;
    hash128_t tid = type_id("IS_A");
    EXPECT_EQ(-1, laplace_relation_lookup(nullptr, &def));
    EXPECT_EQ(-1, laplace_relation_lookup(&tid, nullptr));
}

TEST(LaplaceRelationLaw, ReverseLookupCarriesDefFields) {
    hash128_t isa = type_id("IS_A");
    const laplace_relation_def_t* def = nullptr;
    ASSERT_EQ(0, laplace_relation_lookup(&isa, &def));
    ASSERT_NE(nullptr, def);
    EXPECT_DOUBLE_EQ(0.9, def->rank);                              // taxonomic
    EXPECT_EQ(LAPLACE_REL_SYMMETRY_ASYMMETRIC, def->symmetry);
}

// Chain: the resolved-attestation path orients subject/object by fetching the relation's symmetry
// through laplace_relation_lookup. A symmetric relation must canonicalize (a,b) and (b,a) to the
// same staged id — proving the bucket lookup feeds attestation_orient_resolved correctly.
TEST(LaplaceAttestationEngine, ResolvedSymmetricOrientsViaLookup) {
    hash128_t sym = type_id("IS_SYNONYM_OF");                      // symmetric in the manifest
    hash128_t src = hash_path("src");
    hash128_t a = hash_path("e/a"), b = hash_path("e/b");
    laplace_attestation_staged_t ab, ba;
    ASSERT_EQ(0, laplace_attestation_resolved_build(
        &a, &sym, &b, 0, &src, NULL, 1, 0.7, 1, 1, 0, &ab));
    ASSERT_EQ(0, laplace_attestation_resolved_build(
        &b, &sym, &a, 0, &src, NULL, 1, 0.7, 1, 1, 0, &ba));
    EXPECT_TRUE(hash128_equals(&ab.id, &ba.id));
    EXPECT_TRUE(hash128_equals(&ab.subject_id, &ba.subject_id));
    EXPECT_TRUE(hash128_equals(&ab.object_id, &ba.object_id));
}
