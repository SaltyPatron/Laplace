#include <gtest/gtest.h>

#include "laplace/core/hilbert4d.h"

/* Real test cases land Chunk 1 Story 1.5 — Skilling 2004 known-coord
 * test vectors, locality property (Hamming distance correlates with
 * coord distance), round-trip correctness. Stub for now. */

TEST(LaplaceCoreHilbert4d, StubEncodeProducesAllZeroBytes) {
    double p[4] = {0.5, -0.5, 0.25, -0.25};
    hilbert128_t h;
    hilbert4d_encode(p, &h);
    for (int i = 0; i < 16; ++i) {
        EXPECT_EQ(h.bytes[i], 0u);
    }
}
