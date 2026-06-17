#include <gtest/gtest.h>

#include "laplace/synthesis/arch_template.h"
#include "laplace/synthesis/recipe.h"

static const char* kTinyLlamaConfig = R"({
  "architectures": ["LlamaForCausalLM"],
  "hidden_size": 2048,
  "intermediate_size": 5632,
  "num_attention_heads": 32,
  "num_hidden_layers": 22,
  "num_key_value_heads": 4,
  "torch_dtype": "bfloat16",
  "vocab_size": 32000
})";

TEST(LaplaceSynthesisArchTemplate, LoadLlamaSucceeds) {
    arch_template_t* t = arch_template_load("llama");
    ASSERT_NE(t, nullptr);
    arch_template_free(t);
}

TEST(LaplaceSynthesisArchTemplate, LoadUnknownReturnsNull) {
    arch_template_t* t = arch_template_load("mamba");
    EXPECT_EQ(t, nullptr);
}

TEST(LaplaceSynthesisArchTemplate, RequiredTensorsCountForTinyLlama) {
    arch_template_t* t = arch_template_load("llama");
    ASSERT_NE(t, nullptr);

    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);

    constexpr size_t kExpected = 201;
    constexpr size_t kCap = 256;
    tensor_spec_t specs[kCap];
    int n = arch_template_required_tensors(t, r, specs, kCap);
    EXPECT_EQ(n, (int)kExpected);

    recipe_free(r);
    arch_template_free(t);
}

TEST(LaplaceSynthesisArchTemplate, EmbedTokensIsFirstTensor) {
    arch_template_t* t = arch_template_load("llama");
    ASSERT_NE(t, nullptr);
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);

    tensor_spec_t specs[256];
    int n = arch_template_required_tensors(t, r, specs, 256);
    ASSERT_GT(n, 0);
    EXPECT_STREQ(specs[0].name, "model.embed_tokens.weight");
    EXPECT_EQ(specs[0].rank, 2u);
    EXPECT_EQ(specs[0].shape[0], 32000u);
    EXPECT_EQ(specs[0].shape[1], 2048u);

    recipe_free(r);
    arch_template_free(t);
}

TEST(LaplaceSynthesisArchTemplate, LmHeadIsLastTensor) {
    arch_template_t* t = arch_template_load("llama");
    ASSERT_NE(t, nullptr);
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);

    tensor_spec_t specs[256];
    int n = arch_template_required_tensors(t, r, specs, 256);
    ASSERT_GT(n, 0);
    EXPECT_STREQ(specs[n - 1].name, "lm_head.weight");
    EXPECT_EQ(specs[n - 1].shape[0], 32000u);
    EXPECT_EQ(specs[n - 1].shape[1], 2048u);

    recipe_free(r);
    arch_template_free(t);
}

TEST(LaplaceSynthesisArchTemplate, Layer0QProjShape) {
    arch_template_t* t = arch_template_load("llama");
    ASSERT_NE(t, nullptr);
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);

    tensor_spec_t specs[256];
    int n = arch_template_required_tensors(t, r, specs, 256);
    ASSERT_GT(n, 1);

    EXPECT_STREQ(specs[1].name, "model.layers.0.self_attn.q_proj.weight");
    EXPECT_EQ(specs[1].shape[0], 2048u);
    EXPECT_EQ(specs[1].shape[1], 2048u);

    recipe_free(r);
    arch_template_free(t);
}



static const char* kMiniLmConfig = R"({
  "architectures": ["BertModel"],
  "hidden_size": 384,
  "intermediate_size": 1536,
  "num_attention_heads": 12,
  "num_hidden_layers": 6,
  "torch_dtype": "float32",
  "vocab_size": 30522
})";

TEST(LaplaceSynthesisArchTemplate, AbsentKvHeadsDefaultsToHeadCount) {
    arch_template_t* t = arch_template_load("llama");
    ASSERT_NE(t, nullptr);
    recipe_t* r = recipe_parse(kMiniLmConfig, strlen(kMiniLmConfig));
    ASSERT_NE(r, nullptr);

    tensor_spec_t specs[256];
    int n = arch_template_required_tensors(t, r, specs, 256);
    ASSERT_GT(n, 2);

    EXPECT_STREQ(specs[2].name, "model.layers.0.self_attn.k_proj.weight");
    EXPECT_EQ(specs[2].shape[0], 384u);
    EXPECT_EQ(specs[2].shape[1], 384u);

    recipe_free(r);
    arch_template_free(t);
}

TEST(LaplaceSynthesisArchTemplate, CapTooSmallReturnsCount) {
    arch_template_t* t = arch_template_load("llama");
    ASSERT_NE(t, nullptr);
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);

    tensor_spec_t specs[1];
    int n = arch_template_required_tensors(t, r, specs, 1);
    EXPECT_GT(n, 1);

    recipe_free(r);
    arch_template_free(t);
}
