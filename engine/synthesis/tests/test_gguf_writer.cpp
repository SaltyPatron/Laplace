#include <gtest/gtest.h>

#include <cstring>
#include <cstdio>
#include <vector>

#include "laplace/synthesis/gguf_writer.h"

static const char* kTestPath = "/tmp/laplace_synthesis_test.gguf";

static void cleanup() {
    std::remove(kTestPath);
}

TEST(LaplaceSynthesisGgufWriter, CreateReturnsNonNull) {
    cleanup();
    gguf_writer_t* w = gguf_writer_create(kTestPath);
    ASSERT_NE(w, nullptr);
    gguf_writer_free(w);
    cleanup();
}

TEST(LaplaceSynthesisGgufWriter, FinalizeWritesMagic) {
    cleanup();
    gguf_writer_t* w = gguf_writer_create(kTestPath);
    ASSERT_NE(w, nullptr);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);

    FILE* f = std::fopen(kTestPath, "rb");
    ASSERT_NE(f, nullptr);
    uint8_t hdr[8];
    ASSERT_EQ(std::fread(hdr, 1, 8, f), 8u);
    std::fclose(f);

    EXPECT_EQ(hdr[0], 'G');
    EXPECT_EQ(hdr[1], 'G');
    EXPECT_EQ(hdr[2], 'U');
    EXPECT_EQ(hdr[3], 'F');
    uint32_t ver = (uint32_t)hdr[4] | ((uint32_t)hdr[5] << 8)
                 | ((uint32_t)hdr[6] << 16) | ((uint32_t)hdr[7] << 24);
    EXPECT_EQ(ver, 3u);

    cleanup();
}

TEST(LaplaceSynthesisGgufWriter, AddMetadataStr) {
    cleanup();
    gguf_writer_t* w = gguf_writer_create(kTestPath);
    ASSERT_NE(w, nullptr);
    EXPECT_EQ(gguf_writer_add_metadata_str(w, "general.architecture", "llama"), 0);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);

    FILE* f = std::fopen(kTestPath, "rb");
    ASSERT_NE(f, nullptr);
    std::fseek(f, 0, SEEK_END);
    long sz = std::ftell(f);
    std::fclose(f);
    EXPECT_GT(sz, 32L);

    cleanup();
}

TEST(LaplaceSynthesisGgufWriter, AddMetadataU32) {
    cleanup();
    gguf_writer_t* w = gguf_writer_create(kTestPath);
    ASSERT_NE(w, nullptr);
    EXPECT_EQ(gguf_writer_add_metadata_u32(w, "llama.attention.head_count", 32), 0);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);
    cleanup();
}

TEST(LaplaceSynthesisGgufWriter, AddBF16Tensor) {
    cleanup();
    gguf_writer_t* w = gguf_writer_create(kTestPath);
    ASSERT_NE(w, nullptr);

    const uint16_t data[8] = {0x3F80, 0x3F00, 0x0000, 0x4000,
                               0xBF80, 0x3F80, 0x4080, 0x3F00};
    const size_t shape[2] = {2, 4};
    EXPECT_EQ(gguf_writer_add_tensor(w, "test.weight", 2, shape, 2, data), 0);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);

    FILE* f = std::fopen(kTestPath, "rb");
    ASSERT_NE(f, nullptr);
    std::fseek(f, 0, SEEK_END);
    long sz = std::ftell(f);
    std::fclose(f);
    EXPECT_GT(sz, 64L);

    cleanup();
}

TEST(LaplaceSynthesisGgufWriter, NullPathReturnsNull) {
    gguf_writer_t* w = gguf_writer_create(nullptr);
    EXPECT_EQ(w, nullptr);
}
