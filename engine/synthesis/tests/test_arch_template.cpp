#include <gtest/gtest.h>

#include "laplace/synthesis/arch_template.h"

TEST(LaplaceSynthesisArchTemplate, StubLoadReturnsNull) {
    arch_template_t* t = arch_template_load("llama");
    EXPECT_EQ(t, nullptr);
    arch_template_free(t);
}
