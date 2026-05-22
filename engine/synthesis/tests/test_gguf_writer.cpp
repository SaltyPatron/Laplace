#include <gtest/gtest.h>

#include "laplace/synthesis/gguf_writer.h"

TEST(LaplaceSynthesisGgufWriter, StubCreateReturnsNull) {
    gguf_writer_t* w = gguf_writer_create("/tmp/laplace_test.gguf");
    EXPECT_EQ(w, nullptr);
    gguf_writer_free(w);
}
