#include <gtest/gtest.h>

#include <cmath>
#include <cstring>
#include <vector>

#include "laplace/core/hash_composer.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/math4d.h"
#include "laplace/core/tier_tree.h"

namespace {

/* Synthetic atom resolver: maps atom -> (BLAKE3(atom_le_bytes),
 * coord = atom/1000.0 along x-axis, hilbert = encode(coord)).
 * Deterministic; same atom always produces same triplet. */
int synth_resolver(uint32_t atom, void* /*user*/,
                   hash128_t* out_id, double out_coord[4], hilbert128_t* out_hb) {
    uint8_t bytes[4];
    bytes[0] = (uint8_t)(atom & 0xFF);
    bytes[1] = (uint8_t)((atom >> 8) & 0xFF);
    bytes[2] = (uint8_t)((atom >> 16) & 0xFF);
    bytes[3] = (uint8_t)((atom >> 24) & 0xFF);
    hash128_blake3(bytes, sizeof(bytes), out_id);
    out_coord[0] = (double)atom / 1000.0;
    out_coord[1] = 0.0;
    out_coord[2] = 0.0;
    out_coord[3] = 0.0;
    hilbert4d_encode(out_coord, out_hb);
    return 0;
}

/* Resolver that always returns an error. */
int err_resolver(uint32_t /*atom*/, void* /*user*/,
                 hash128_t* /*id*/, double /*coord*/[4], hilbert128_t* /*hb*/) {
    return -42;
}

tier_tree_t* sample_tree() {
    tier_tree_t* t = tier_tree_new(8);
    tier_tree_add_leaf(t, 0, 100, 0, 1);
    tier_tree_add_leaf(t, 0, 101, 1, 1);
    tier_tree_add_leaf(t, 0, 102, 2, 1);
    tier_tree_add_leaf(t, 0, 103, 3, 1);
    tier_tree_add_node(t, 1, 0, 2, 0, 2);  /* idx 4 (children 0,1) */
    tier_tree_add_node(t, 1, 2, 2, 2, 2);  /* idx 5 (children 2,3) */
    tier_tree_add_node(t, 2, 4, 2, 0, 4);  /* idx 6 = root (children 4,5) */
    return t;
}

} // namespace

TEST(LaplaceCoreHashComposer, RejectsNullArgs) {
    EXPECT_NE(0, hash_composer_run(nullptr, synth_resolver, nullptr));
    tier_tree_t* t = sample_tree();
    EXPECT_NE(0, hash_composer_run(t, nullptr, nullptr));
    tier_tree_free(t);
}

TEST(LaplaceCoreHashComposer, EmptyTreeIsSuccess) {
    tier_tree_t* t = tier_tree_new(0);
    EXPECT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    tier_tree_free(t);
}

TEST(LaplaceCoreHashComposer, ResolverErrorPropagates) {
    tier_tree_t* t = tier_tree_new(1);
    tier_tree_add_leaf(t, 0, 1, 0, 0);
    EXPECT_EQ(-42, hash_composer_run(t, err_resolver, nullptr));
    tier_tree_free(t);
}

TEST(LaplaceCoreHashComposer, LeafIdMatchesResolver) {
    tier_tree_t* t = tier_tree_new(1);
    tier_tree_add_leaf(t, 0, 42, 0, 0);
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, 0, &v));
    hash128_t expected;
    double dummy_coord[4];
    hilbert128_t dummy_hb;
    synth_resolver(42, nullptr, &expected, dummy_coord, &dummy_hb);
    EXPECT_TRUE(hash128_equals(&v.id, &expected));
    EXPECT_EQ(0.042, v.coord[0]);
    tier_tree_free(t);
}

TEST(LaplaceCoreHashComposer, InteriorIdMatchesMerkleOfChildren) {
    tier_tree_t* t = sample_tree();
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));

    /* Independently compute the expected id of inter0 (idx 4) using
     * the same synth_resolver + hash128_merkle. */
    hash128_t leaf0, leaf1;
    double c0[4], c1[4];
    hilbert128_t h0, h1;
    synth_resolver(100, nullptr, &leaf0, c0, &h0);
    synth_resolver(101, nullptr, &leaf1, c1, &h1);
    hash128_t children[2] = {leaf0, leaf1};
    hash128_t expected_inter0;
    hash128_merkle(1, children, 2, &expected_inter0);

    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, 4, &v));
    EXPECT_TRUE(hash128_equals(&v.id, &expected_inter0));
    tier_tree_free(t);
}

TEST(LaplaceCoreHashComposer, InteriorCoordIsChildCentroid) {
    tier_tree_t* t = sample_tree();
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    /* Inter0 children: atom 100 (x=0.100), atom 101 (x=0.101).
     * Centroid x = (0.100 + 0.101)/2 = 0.1005. */
    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, 4, &v));
    EXPECT_DOUBLE_EQ(0.1005, v.coord[0]);
    EXPECT_DOUBLE_EQ(0.0, v.coord[1]);
}

TEST(LaplaceCoreHashComposer, RootIdMatchesMerkleOfMerkles) {
    tier_tree_t* t = sample_tree();
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    /* Recompute root by walking from leaves */
    hash128_t leaves[4];
    double coords[4][4];
    hilbert128_t hbs[4];
    const uint32_t atoms[4] = {100, 101, 102, 103};
    for (int i = 0; i < 4; ++i) {
        synth_resolver(atoms[i], nullptr, &leaves[i], coords[i], &hbs[i]);
    }
    hash128_t inter0_children[2] = {leaves[0], leaves[1]};
    hash128_t inter1_children[2] = {leaves[2], leaves[3]};
    hash128_t inter0, inter1;
    hash128_merkle(1, inter0_children, 2, &inter0);
    hash128_merkle(1, inter1_children, 2, &inter1);
    hash128_t inter_arr[2] = {inter0, inter1};
    hash128_t root;
    hash128_merkle(2, inter_arr, 2, &root);

    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, 6, &v));
    EXPECT_TRUE(hash128_equals(&v.id, &root));
    tier_tree_free(t);
}

TEST(LaplaceCoreHashComposer, RootCoordIsCentroidOfInteriorCentroids) {
    tier_tree_t* t = sample_tree();
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    /* Inter0.x = 0.1005, Inter1.x = (0.102+0.103)/2 = 0.1025.
     * Root.x = (0.1005 + 0.1025)/2 = 0.1015 */
    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, 6, &v));
    EXPECT_DOUBLE_EQ(0.1015, v.coord[0]);
}

TEST(LaplaceCoreHashComposer, IsDeterministicAcrossRuns) {
 /* : same input → byte-identical populated tree. Run twice on
     * separately-built sample trees and compare the populated state. */
    tier_tree_t* a = sample_tree();
    tier_tree_t* b = sample_tree();
    ASSERT_EQ(0, hash_composer_run(a, synth_resolver, nullptr));
    ASSERT_EQ(0, hash_composer_run(b, synth_resolver, nullptr));
    const size_t n = tier_tree_node_count(a);
    ASSERT_EQ(n, tier_tree_node_count(b));
    for (uint32_t i = 0; i < n; ++i) {
        tier_node_view_t va, vb;
        ASSERT_EQ(0, tier_tree_get_node(a, i, &va));
        ASSERT_EQ(0, tier_tree_get_node(b, i, &vb));
        EXPECT_TRUE(hash128_equals(&va.id, &vb.id)) << "id mismatch at " << i;
        EXPECT_EQ(va.coord[0], vb.coord[0]) << "coord mismatch at " << i;
        for (int j = 0; j < 16; ++j) {
            EXPECT_EQ(va.hilbert.bytes[j], vb.hilbert.bytes[j])
                << "hilbert byte " << j << " mismatch at " << i;
        }
    }
    tier_tree_free(a);
    tier_tree_free(b);
}

TEST(LaplaceCoreHashComposer, HilbertEncodesPopulatedCoord) {
    /* For every non-leaf node, hilbert == hilbert4d_encode(coord). */
    tier_tree_t* t = sample_tree();
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    const size_t n = tier_tree_node_count(t);
    for (uint32_t i = 0; i < n; ++i) {
        tier_node_view_t v;
        ASSERT_EQ(0, tier_tree_get_node(t, i, &v));
        hilbert128_t expected;
        hilbert4d_encode(v.coord, &expected);
        for (int j = 0; j < 16; ++j) {
            EXPECT_EQ(v.hilbert.bytes[j], expected.bytes[j])
                << "byte " << j << " idx " << i;
        }
    }
    tier_tree_free(t);
}

TEST(LaplaceCoreHashComposer, BottomUpOrderingObserved) {
    /* If we read mid-walk, parents would have stale ids. The contract is:
     * after hash_composer_run returns, ALL nodes are populated. We
     * indirectly verify by confirming the root id matches an independent
     * recomputation (RootIdMatchesMerkleOfMerkles already does this);
     * here we just assert that the LAST node added has a non-zero id. */
    tier_tree_t* t = sample_tree();
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, tier_tree_node_count(t) - 1, &v));
    hash128_t zero; hash128_zero(&zero);
    EXPECT_FALSE(hash128_equals(&v.id, &zero));
}

TEST(LaplaceCoreHashComposer, ScalesTo1KNodes) {
    /* 1000 leaves under one root; sanity-check the walk doesn't blow up. */
    tier_tree_t* t = tier_tree_new(1024);
    const uint32_t N = 1000;
    for (uint32_t i = 0; i < N; ++i) tier_tree_add_leaf(t, 0, i, i, 1);
    tier_tree_add_node(t, 1, 0, N, 0, N);
    ASSERT_EQ(0, hash_composer_run(t, synth_resolver, nullptr));
    tier_node_view_t root_v;
    ASSERT_EQ(0, tier_tree_get_node(t, N, &root_v));
    hash128_t zero; hash128_zero(&zero);
    EXPECT_FALSE(hash128_equals(&root_v.id, &zero));
    tier_tree_free(t);
}
