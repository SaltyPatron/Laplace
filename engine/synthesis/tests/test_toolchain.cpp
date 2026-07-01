#include <gtest/gtest.h>

#include <vector>

#include "laplace/synthesis/init.h"
#include "laplace/synthesis/tensor_decompose.h"

TEST(SynthesisToolchain, MklSvdNotStub) {
    ASSERT_EQ(laplace_synthesis_init(), 0);
    std::vector<float> A(4 * 3, 0.0f);
    A[0] = 10.0f;
    A[1 * 3 + 1] = 0.01f;
    const size_t m = 4, n = 3, kmax = 3;
    std::vector<float> U(m * kmax), S(kmax), Vt(kmax * n);
    size_t r = 0;
    const int rc = tensor_svd_truncate(A.data(), m, n, 0.0, &r, U.data(), S.data(), Vt.data(), kmax);
    ASSERT_NE(rc, -2) << "tensor_svd_truncate stub (-2) — MKL not linked; reconfigure with -DLAPLACE_SYNTHESIS_REQUIRE_MKL=ON";
    ASSERT_EQ(rc, 0);
    EXPECT_GE(r, 1u);
}
