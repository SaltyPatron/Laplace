#include <gtest/gtest.h>

#include <cstring>
#include <vector>

#include "laplace/core/tier_tree.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

namespace {

/* Helper: build a tiny 3-level tree:
 *
 *       root (tier=2, idx=6)
 *       / \
 *  inter0  inter1   (tier=1, idx=4,5)
 *   / \      |
 *  L0 L1    L2-L3  (tier=0, idx=0..3, atoms 100..103)
 *
 * Returns the tree (caller frees) with root index in *out_root.
 */
tier_tree_t* build_sample_tree(uint32_t* out_root) {
    tier_tree_t* t = tier_tree_new(8);
    EXPECT_NE(nullptr, t);
    /* leaves (4) */
    EXPECT_EQ(0u, tier_tree_add_leaf(t, 0, 100, 0, 1));
    EXPECT_EQ(1u, tier_tree_add_leaf(t, 0, 101, 1, 1));
    EXPECT_EQ(2u, tier_tree_add_leaf(t, 0, 102, 2, 1));
    EXPECT_EQ(3u, tier_tree_add_leaf(t, 0, 103, 3, 1));
    /* inter0: children [0..1] */
    EXPECT_EQ(4u, tier_tree_add_node(t, 1, 0, 2, 0, 2));
    /* inter1: children [2..3] */
    EXPECT_EQ(5u, tier_tree_add_node(t, 1, 2, 2, 2, 2));
    /* root: children [4..5] */
    EXPECT_EQ(6u, tier_tree_add_node(t, 2, 4, 2, 0, 4));
    EXPECT_EQ(0, tier_tree_finalize(t));
    *out_root = 6;
    return t;
}

} // namespace

TEST(LaplaceCoreTierTree, NewWithZeroCapacityProducesEmpty) {
    tier_tree_t* t = tier_tree_new(0);
    ASSERT_NE(nullptr, t);
    EXPECT_EQ(0u, tier_tree_node_count(t));
    EXPECT_EQ(0u, tier_tree_capacity(t));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, NewWithCapacityHintAllocates) {
    tier_tree_t* t = tier_tree_new(128);
    ASSERT_NE(nullptr, t);
    EXPECT_GE(tier_tree_capacity(t), 128u);
    EXPECT_EQ(0u, tier_tree_node_count(t));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, FreeNullIsSafe) {
    tier_tree_free(nullptr);  /* must not crash */
    SUCCEED();
}

TEST(LaplaceCoreTierTree, AddLeafReturnsSequentialIndices) {
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    EXPECT_EQ(0u, tier_tree_add_leaf(t, 0, 65, 0, 1));   /* 'A' */
    EXPECT_EQ(1u, tier_tree_add_leaf(t, 0, 66, 1, 1));   /* 'B' */
    EXPECT_EQ(2u, tier_tree_add_leaf(t, 0, 67, 2, 1));   /* 'C' */
    EXPECT_EQ(3u, tier_tree_node_count(t));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, AddNodeRejectsChildRangeOutOfBounds) {
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    /* No nodes yet — referring to children [0..1] is invalid */
    EXPECT_EQ(TIER_TREE_INVALID, tier_tree_add_node(t, 1, 0, 2, 0, 2));
    EXPECT_EQ(0u, tier_tree_node_count(t));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, AddNodeRejectsMalformedSentinel) {
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    /* child_count==0 + first_child_idx not sentinel is malformed */
    EXPECT_EQ(TIER_TREE_INVALID, tier_tree_add_node(t, 1, 0, 0, 0, 0));
    /* child_count>0 + first_child_idx==INVALID is malformed */
    EXPECT_EQ(TIER_TREE_INVALID, tier_tree_add_node(t, 1, TIER_TREE_INVALID, 1, 0, 0));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, AddNodeWithZeroChildrenIsValidEmptyInterior) {
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    EXPECT_EQ(0u, tier_tree_add_node(t, 1, TIER_TREE_INVALID, 0, 0, 0));
    EXPECT_EQ(1u, tier_tree_node_count(t));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, GetNodePopulatesView) {
    uint32_t root = TIER_TREE_INVALID;
    tier_tree_t* t = build_sample_tree(&root);
    tier_node_view_t v;

    EXPECT_EQ(0, tier_tree_get_node(t, 0, &v));
    EXPECT_EQ(0, v.tier);
    EXPECT_EQ(100u, v.atom);
    EXPECT_EQ(TIER_TREE_INVALID, v.first_child_idx);
    EXPECT_EQ(0u, v.child_count);

    EXPECT_EQ(0, tier_tree_get_node(t, 4, &v));
    EXPECT_EQ(1, v.tier);
    EXPECT_EQ(0u, v.first_child_idx);
    EXPECT_EQ(2u, v.child_count);

    EXPECT_EQ(0, tier_tree_get_node(t, root, &v));
    EXPECT_EQ(2, v.tier);
    EXPECT_EQ(4u, v.first_child_idx);
    EXPECT_EQ(2u, v.child_count);

    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, GetNodeRejectsOutOfBounds) {
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    tier_tree_add_leaf(t, 0, 1, 0, 0);
    tier_node_view_t v;
    EXPECT_NE(0, tier_tree_get_node(t, 1, &v));   /* idx == count */
    EXPECT_NE(0, tier_tree_get_node(t, 100, &v)); /* way past */
    EXPECT_NE(0, tier_tree_get_node(t, 0, nullptr));
    EXPECT_NE(0, tier_tree_get_node(nullptr, 0, &v));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, FinalizeSetsParentForEveryNonRootNode) {
    uint32_t root = TIER_TREE_INVALID;
    tier_tree_t* t = build_sample_tree(&root);
    tier_node_view_t v;
    /* leaves 0..1 -> parent 4 */
    EXPECT_EQ(0, tier_tree_get_node(t, 0, &v)); EXPECT_EQ(4u, v.parent_idx);
    EXPECT_EQ(0, tier_tree_get_node(t, 1, &v)); EXPECT_EQ(4u, v.parent_idx);
    /* leaves 2..3 -> parent 5 */
    EXPECT_EQ(0, tier_tree_get_node(t, 2, &v)); EXPECT_EQ(5u, v.parent_idx);
    EXPECT_EQ(0, tier_tree_get_node(t, 3, &v)); EXPECT_EQ(5u, v.parent_idx);
    /* inter 4..5 -> parent 6 */
    EXPECT_EQ(0, tier_tree_get_node(t, 4, &v)); EXPECT_EQ(6u, v.parent_idx);
    EXPECT_EQ(0, tier_tree_get_node(t, 5, &v)); EXPECT_EQ(6u, v.parent_idx);
    /* root -> no parent */
    EXPECT_EQ(0, tier_tree_get_node(t, root, &v)); EXPECT_EQ(TIER_TREE_INVALID, v.parent_idx);
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, FinalizeIsIdempotent) {
    uint32_t root = TIER_TREE_INVALID;
    tier_tree_t* t = build_sample_tree(&root);
    /* second finalize must produce identical state */
    EXPECT_EQ(0, tier_tree_finalize(t));
    tier_node_view_t v;
    EXPECT_EQ(0, tier_tree_get_node(t, 0, &v));
    EXPECT_EQ(4u, v.parent_idx);
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, IdCoordHilbertZeroPreCompose) {
    uint32_t root = TIER_TREE_INVALID;
    tier_tree_t* t = build_sample_tree(&root);
    hash128_t zero; hash128_zero(&zero);
    for (uint32_t i = 0; i <= root; ++i) {
        tier_node_view_t v;
        ASSERT_EQ(0, tier_tree_get_node(t, i, &v));
        EXPECT_TRUE(hash128_equals(&v.id, &zero)) << "idx " << i;
        EXPECT_EQ(0.0, v.coord[0]);
        EXPECT_EQ(0.0, v.coord[1]);
        EXPECT_EQ(0.0, v.coord[2]);
        EXPECT_EQ(0.0, v.coord[3]);
        for (int b = 0; b < 16; ++b) {
            EXPECT_EQ(0, v.hilbert.bytes[b]) << "idx " << i << " byte " << b;
        }
    }
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, SetIdSetCoordSetHilbertRoundTrip) {
    tier_tree_t* t = tier_tree_new(2);
    ASSERT_NE(nullptr, t);
    tier_tree_add_leaf(t, 0, 1, 0, 0);

    hash128_t h = { .hi = 0x0102030405060708ull, .lo = 0x090A0B0C0D0E0F10ull };
    EXPECT_EQ(0, tier_tree_set_id(t, 0, &h));

    double c[4] = { 0.1, 0.2, 0.3, 0.4 };
    EXPECT_EQ(0, tier_tree_set_coord(t, 0, c));

    hilbert128_t hb;
    for (int i = 0; i < 16; ++i) hb.bytes[i] = (uint8_t)(i + 1);
    EXPECT_EQ(0, tier_tree_set_hilbert(t, 0, &hb));

    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, 0, &v));
    EXPECT_TRUE(hash128_equals(&v.id, &h));
    EXPECT_EQ(0.1, v.coord[0]);
    EXPECT_EQ(0.4, v.coord[3]);
    for (int i = 0; i < 16; ++i) EXPECT_EQ(i + 1, v.hilbert.bytes[i]);

    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, SettersRejectOutOfBounds) {
    tier_tree_t* t = tier_tree_new(1);
    ASSERT_NE(nullptr, t);
    hash128_t h; hash128_zero(&h);
    double c[4] = {0};
    hilbert128_t hb; memset(&hb, 0, sizeof(hb));
    EXPECT_NE(0, tier_tree_set_id(t, 0, &h));        /* count is 0 */
    EXPECT_NE(0, tier_tree_set_coord(t, 0, c));
    EXPECT_NE(0, tier_tree_set_hilbert(t, 0, &hb));
    EXPECT_NE(0, tier_tree_set_id(nullptr, 0, &h));
    EXPECT_NE(0, tier_tree_set_id(t, 0, nullptr));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, BulkAccessorsReturnContiguousSoa) {
    uint32_t root = TIER_TREE_INVALID;
    tier_tree_t* t = build_sample_tree(&root);
    const uint8_t*  tiers     = tier_tree_tier_array(t);
    const uint32_t* atoms     = tier_tree_atom_array(t);
    const uint32_t* fci       = tier_tree_first_child_idx_array(t);
    const uint32_t* cc        = tier_tree_child_count_array(t);
    const uint32_t* parents   = tier_tree_parent_idx_array(t);
    ASSERT_NE(nullptr, tiers);
    ASSERT_NE(nullptr, atoms);
    ASSERT_NE(nullptr, fci);
    ASSERT_NE(nullptr, cc);
    ASSERT_NE(nullptr, parents);

    EXPECT_EQ(0, tiers[0]);
    EXPECT_EQ(0, tiers[3]);
    EXPECT_EQ(1, tiers[4]);
    EXPECT_EQ(1, tiers[5]);
    EXPECT_EQ(2, tiers[root]);

    EXPECT_EQ(100u, atoms[0]);
    EXPECT_EQ(103u, atoms[3]);

    EXPECT_EQ(0u, fci[4]);  EXPECT_EQ(2u, cc[4]);
    EXPECT_EQ(2u, fci[5]);  EXPECT_EQ(2u, cc[5]);
    EXPECT_EQ(4u, fci[root]); EXPECT_EQ(2u, cc[root]);

    EXPECT_EQ(4u, parents[0]);
    EXPECT_EQ(6u, parents[4]);
    EXPECT_EQ(TIER_TREE_INVALID, parents[root]);

    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, MutableArraysAllowComposerFill) {
    uint32_t root = TIER_TREE_INVALID;
    tier_tree_t* t = build_sample_tree(&root);
    hash128_t* ids = tier_tree_id_array_mut(t);
    double*    coords = tier_tree_coord_array_mut(t);
    ASSERT_NE(nullptr, ids);
    ASSERT_NE(nullptr, coords);

    /* Simulate hash_composer filling root id + coord */
    ids[root].hi = 0xDEADBEEFCAFEBABEull;
    ids[root].lo = 0x0123456789ABCDEFull;
    coords[root * 4 + 0] = 0.5;
    coords[root * 4 + 3] = -0.25;

    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, root, &v));
    EXPECT_EQ(0xDEADBEEFCAFEBABEull, v.id.hi);
    EXPECT_EQ(0x0123456789ABCDEFull, v.id.lo);
    EXPECT_EQ(0.5, v.coord[0]);
    EXPECT_EQ(-0.25, v.coord[3]);

    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, GrowsBeyondCapacityHint) {
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    const uint32_t N = 1000;
    for (uint32_t i = 0; i < N; ++i) {
        ASSERT_EQ(i, tier_tree_add_leaf(t, 0, i, i, 1));
    }
    EXPECT_EQ(N, tier_tree_node_count(t));
    EXPECT_GE(tier_tree_capacity(t), N);
    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, N - 1, &v));
    EXPECT_EQ(N - 1, v.atom);
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, AddNodeRejectsOverflowingChildRange) {
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    tier_tree_add_leaf(t, 0, 1, 0, 0);
    /* first_child_idx + child_count would wrap u32 -> reject */
    EXPECT_EQ(TIER_TREE_INVALID,
              tier_tree_add_node(t, 1, UINT32_MAX - 1, 5, 0, 0));
    tier_tree_free(t);
}

TEST(LaplaceCoreTierTree, ParentIdxInvariantHoldsBeforeFinalize) {
    /* Before finalize, parent_idx is INVALID for every node. After
     * finalize, it's INVALID only for the root. */
    tier_tree_t* t = tier_tree_new(4);
    ASSERT_NE(nullptr, t);
    tier_tree_add_leaf(t, 0, 1, 0, 0);
    tier_tree_add_leaf(t, 0, 2, 0, 0);
    tier_tree_add_node(t, 1, 0, 2, 0, 0);
    tier_node_view_t v;
    ASSERT_EQ(0, tier_tree_get_node(t, 0, &v));
    EXPECT_EQ(TIER_TREE_INVALID, v.parent_idx);
    ASSERT_EQ(0, tier_tree_finalize(t));
    ASSERT_EQ(0, tier_tree_get_node(t, 0, &v));
    EXPECT_EQ(2u, v.parent_idx);
    tier_tree_free(t);
}
