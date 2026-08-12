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

// GH #1033 — compute_substrate_gram's sparse SB fold must be a function of the
// EDGE SET, not of the caller's traversal order.
//
// Construction is deliberately adversarial rather than incidental: every edge
// targets row 0 so all four products land in the same accumulator, and the
// weights are chosen so that summation order decides the result under IEEE-754.
// 1e-16 is below half an ulp of 1.0 (~1.11e-16), so 1.0 + 1e-16 + 1e-16 absorbs
// both small terms and leaves exactly 1.0, while -1.0 + 1.0 + 1e-16 + 1e-16
// cancels first and retains them. Feed the same four edges in two orders and an
// order-dependent fold returns two different doubles.
//
// This test FAILS if the canonical sort in compute_substrate_gram is removed —
// verified by reverting the sort locally before committing.
TEST(LaplaceSynthesisArchTemplate, SubstrateGramIsEdgeOrderInvariant) {
    constexpr std::size_t kVocab = 4, kDim = 1;
    const double token_basis[kVocab * kDim] = {1.0, 1.0, 1.0, 1.0};
    const double per_token[kVocab]          = {0.0, 0.0, 0.0, 0.0};

    // Same set, two traversals.
    const int    rowsA[4] = {0, 0, 0, 0};
    const int    colsA[4] = {1, 2, 3, 0};
    const double valsA[4] = {1.0, 1e-16, 1e-16, -1.0};

    const int    rowsB[4] = {0, 0, 0, 0};
    const int    colsB[4] = {0, 3, 2, 1};
    const double valsB[4] = {-1.0, 1e-16, 1e-16, 1.0};

    double unaryA[kDim * kDim] = {0.0}, binaryA[kDim * kDim] = {0.0};
    double unaryB[kDim * kDim] = {0.0}, binaryB[kDim * kDim] = {0.0};

    int rcA = compute_substrate_gram(token_basis, per_token, kVocab, kDim,
                                     rowsA, colsA, valsA, 4, unaryA, binaryA);
    if (rcA == -2) GTEST_SKIP() << "built without MKL; compute_substrate_gram is a no-op";
    ASSERT_EQ(rcA, 0);

    int rcB = compute_substrate_gram(token_basis, per_token, kVocab, kDim,
                                     rowsB, colsB, valsB, 4, unaryB, binaryB);
    ASSERT_EQ(rcB, 0);

    // Bitwise, not near: the whole point is reproducibility, and this feeds a
    // Gram matrix that feeds a decomposition.
    EXPECT_EQ(binaryA[0], binaryB[0])
        << "binary_gram depends on edge traversal order: "
        << binaryA[0] << " vs " << binaryB[0];
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
