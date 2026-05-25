#include <gtest/gtest.h>

#include "laplace/synthesis/recipe.h"

static const char* kTinyLlamaConfig = R"({
  "architectures": ["LlamaForCausalLM"],
  "hidden_act": "silu",
  "hidden_size": 2048,
  "intermediate_size": 5632,
  "model_type": "llama",
  "num_attention_heads": 32,
  "num_hidden_layers": 22,
  "num_key_value_heads": 4,
  "rope_theta": 10000.0,
  "torch_dtype": "bfloat16",
  "vocab_size": 32000
})";

TEST(LaplaceSynthesisRecipe, ParsesNonNull) {
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);
    recipe_free(r);
}

TEST(LaplaceSynthesisRecipe, ExtractsIntegerFields) {
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);
    EXPECT_STREQ(recipe_get_field(r, "hidden_size"),         "2048");
    EXPECT_STREQ(recipe_get_field(r, "num_hidden_layers"),   "22");
    EXPECT_STREQ(recipe_get_field(r, "num_attention_heads"), "32");
    EXPECT_STREQ(recipe_get_field(r, "num_key_value_heads"), "4");
    EXPECT_STREQ(recipe_get_field(r, "intermediate_size"),   "5632");
    EXPECT_STREQ(recipe_get_field(r, "vocab_size"),          "32000");
    recipe_free(r);
}

TEST(LaplaceSynthesisRecipe, ExtractsStringFields) {
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);
    EXPECT_STREQ(recipe_get_field(r, "torch_dtype"), "bfloat16");
    EXPECT_STREQ(recipe_get_field(r, "hidden_act"),  "silu");
    EXPECT_STREQ(recipe_get_field(r, "model_type"),  "llama");
    recipe_free(r);
}

TEST(LaplaceSynthesisRecipe, ExtractsArchitecturesFirstElement) {
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);
    const char* arch = recipe_get_field(r, "architectures");
    ASSERT_NE(arch, nullptr);
    EXPECT_STREQ(arch, "LlamaForCausalLM");
    recipe_free(r);
}

TEST(LaplaceSynthesisRecipe, MissingFieldReturnsNull) {
    recipe_t* r = recipe_parse(kTinyLlamaConfig, strlen(kTinyLlamaConfig));
    ASSERT_NE(r, nullptr);
    EXPECT_EQ(recipe_get_field(r, "nonexistent_field"), nullptr);
    recipe_free(r);
}

TEST(LaplaceSynthesisRecipe, NullInputReturnsNull) {
    EXPECT_EQ(recipe_parse(nullptr, 0), nullptr);
    EXPECT_EQ(recipe_parse("{}", 0), nullptr);
}

TEST(LaplaceSynthesisRecipe, EmptyObjectParsesOk) {
    recipe_t* r = recipe_parse("{}", 2);
    EXPECT_NE(r, nullptr);
    EXPECT_EQ(recipe_get_field(r, "anything"), nullptr);
    recipe_free(r);
}
