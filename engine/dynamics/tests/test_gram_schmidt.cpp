#include <gtest/gtest.h>

#include "laplace/dynamics/gram_schmidt.h"

/* Real tests land Chunk 6 Story 6.7 — orthonormalization of a basis;
 * verify pairwise dot products are ~0 and norms are ~1. Stub for now. */

TEST(LaplaceDynamicsGramSchmidt, StubReturnsError) {
    double vecs[6] = {1,0,0, 0,1,0};
    int rc = gram_schmidt_orthonormalize(vecs, 2, 3);
    EXPECT_NE(rc, 0);
}
