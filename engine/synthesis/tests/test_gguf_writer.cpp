#include <gtest/gtest.h>

#include <cstring>
#include <cstdio>
#include <string>
#include <vector>

#include "laplace/synthesis/gguf_writer.h"

// Each gtest is a separate ctest process under gtest_discover_tests + -jN.
// A shared path races: one test's cleanup() deletes another's just-written file.
static std::string testPath() {
    const ::testing::TestInfo* info =
        ::testing::UnitTest::GetInstance()->current_test_info();
    return std::string("/tmp/laplace_gguf_") + info->name() + ".gguf";
}

static void cleanup(const std::string& path) {
    std::remove(path.c_str());
}

TEST(LaplaceSynthesisGgufWriter, CreateReturnsNonNull) {
    const std::string path = testPath();
    cleanup(path);
    gguf_writer_t* w = gguf_writer_create(path.c_str());
    ASSERT_NE(w, nullptr);
    gguf_writer_free(w);
    cleanup(path);
}

TEST(LaplaceSynthesisGgufWriter, FinalizeWritesMagic) {
    const std::string path = testPath();
    cleanup(path);
    gguf_writer_t* w = gguf_writer_create(path.c_str());
    ASSERT_NE(w, nullptr);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);

    FILE* f = std::fopen(path.c_str(), "rb");
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

    cleanup(path);
}

TEST(LaplaceSynthesisGgufWriter, AddMetadataStr) {
    const std::string path = testPath();
    cleanup(path);
    gguf_writer_t* w = gguf_writer_create(path.c_str());
    ASSERT_NE(w, nullptr);
    EXPECT_EQ(gguf_writer_add_metadata_str(w, "general.architecture", "llama"), 0);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);

    FILE* f = std::fopen(path.c_str(), "rb");
    ASSERT_NE(f, nullptr);
    std::fseek(f, 0, SEEK_END);
    long sz = std::ftell(f);
    std::fclose(f);
    EXPECT_GT(sz, 32L);

    cleanup(path);
}

TEST(LaplaceSynthesisGgufWriter, AddMetadataU32) {
    const std::string path = testPath();
    cleanup(path);
    gguf_writer_t* w = gguf_writer_create(path.c_str());
    ASSERT_NE(w, nullptr);
    EXPECT_EQ(gguf_writer_add_metadata_u32(w, "llama.attention.head_count", 32), 0);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);
    cleanup(path);
}

TEST(LaplaceSynthesisGgufWriter, AddBF16Tensor) {
    const std::string path = testPath();
    cleanup(path);
    gguf_writer_t* w = gguf_writer_create(path.c_str());
    ASSERT_NE(w, nullptr);

    const uint16_t data[8] = {0x3F80, 0x3F00, 0x0000, 0x4000,
                               0xBF80, 0x3F80, 0x4080, 0x3F00};
    const size_t shape[2] = {2, 4};
    EXPECT_EQ(gguf_writer_add_tensor(w, "test.weight", 2, shape, 2, data), 0);
    EXPECT_EQ(gguf_writer_finalize(w), 0);
    gguf_writer_free(w);

    FILE* f = std::fopen(path.c_str(), "rb");
    ASSERT_NE(f, nullptr);
    std::fseek(f, 0, SEEK_END);
    long sz = std::ftell(f);
    std::fclose(f);
    EXPECT_GT(sz, 64L);

    cleanup(path);
}

TEST(LaplaceSynthesisGgufWriter, NullPathReturnsNull) {
    gguf_writer_t* w = gguf_writer_create(nullptr);
    EXPECT_EQ(w, nullptr);
}

// ---- HF -> GGML tensor naming (#273) ----------------------------------------
// A writer that emits HuggingFace names produces a file no llama.cpp build loads,
// so this mapping is load-bearing format grammar, not cosmetics.

namespace {
std::string MapName(const char* hf) {
    char buf[256];
    int n = gguf_tensor_name_hf_to_ggml(hf, buf, sizeof buf);
    EXPECT_GE(n, 0) << hf;
    return std::string(buf, (size_t)(n < 0 ? 0 : n));
}
}  // namespace

TEST(LaplaceGgufTensorNames, TopLevelTensors) {
    EXPECT_EQ(MapName("model.embed_tokens.weight"), "token_embd.weight");
    EXPECT_EQ(MapName("model.norm.weight"), "output_norm.weight");
    EXPECT_EQ(MapName("lm_head.weight"), "output.weight");
}

TEST(LaplaceGgufTensorNames, PerLayerTensorsKeepTheirIndex) {
    EXPECT_EQ(MapName("model.layers.0.self_attn.q_proj.weight"), "blk.0.attn_q.weight");
    EXPECT_EQ(MapName("model.layers.7.self_attn.k_proj.weight"), "blk.7.attn_k.weight");
    EXPECT_EQ(MapName("model.layers.21.self_attn.v_proj.weight"), "blk.21.attn_v.weight");
    EXPECT_EQ(MapName("model.layers.3.self_attn.o_proj.weight"), "blk.3.attn_output.weight");
    EXPECT_EQ(MapName("model.layers.3.mlp.gate_proj.weight"), "blk.3.ffn_gate.weight");
    EXPECT_EQ(MapName("model.layers.3.mlp.up_proj.weight"), "blk.3.ffn_up.weight");
    EXPECT_EQ(MapName("model.layers.3.mlp.down_proj.weight"), "blk.3.ffn_down.weight");
    EXPECT_EQ(MapName("model.layers.3.input_layernorm.weight"), "blk.3.attn_norm.weight");
    EXPECT_EQ(MapName("model.layers.3.post_attention_layernorm.weight"), "blk.3.ffn_norm.weight");
}

TEST(LaplaceGgufTensorNames, UnknownNamesPassThroughRatherThanBeingDropped) {
    EXPECT_EQ(MapName("some.future.tensor"), "some.future.tensor");
    // unknown per-layer suffix keeps the blk.N rewrite but preserves the suffix
    EXPECT_EQ(MapName("model.layers.5.brand_new.weight"), "blk.5.brand_new.weight");
    // a layer prefix with no suffix is not a layer tensor
    EXPECT_EQ(MapName("model.layers."), "model.layers.");
}

TEST(LaplaceGgufTensorNames, GuardsAndCapacity) {
    char buf[8];
    EXPECT_EQ(gguf_tensor_name_hf_to_ggml(nullptr, buf, sizeof buf), -1);
    EXPECT_EQ(gguf_tensor_name_hf_to_ggml("x", nullptr, 8), -1);
    EXPECT_EQ(gguf_tensor_name_hf_to_ggml("x", buf, 0), -1);
    // too small for the mapped name -> refuse, never truncate a tensor name
    EXPECT_EQ(gguf_tensor_name_hf_to_ggml("model.embed_tokens.weight", buf, sizeof buf), -1);
}
