#include <gtest/gtest.h>

#include "laplace/dynamics/sparsity.h"

/* Real tests land Chunk 6 Stories 6.10/6.11/6.12 — lottery-ticket-aware
 * multi-pass filter. Stub for now. */

TEST(LaplaceDynamicsSparsity, StubPerTensorReturnsError) {
    double weights[10] = {1,2,3,4,5,6,7,8,9,10};
    uint8_t mask[10] = {};
    sparsity_params_t params = {0.5, 2};
    int rc = sparsity_per_tensor_topk(weights, 10, &params, mask);
    EXPECT_NE(rc, 0);
}
