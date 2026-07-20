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

// ---- typed accessors (#263) --------------------------------------------------
// These exist so no caller re-parses config.json in its own language just to read
// a number out of it. A malformed dimension must fail loudly, never read as 0.

TEST(LaplaceSynthesisRecipe, TypedIntAndDoubleReads) {
    const char* json =
        "{\"hidden_size\": 4096, \"num_hidden_layers\": 32, \"rope_theta\": 10000.0,"
        " \"rms_norm_eps\": 1e-05, \"model_type\": \"llama\", \"neg\": -7}";
    recipe_t* r = recipe_parse(json, strlen(json));
    ASSERT_NE(r, nullptr);

    long long i = 0;
    EXPECT_EQ(recipe_get_int(r, "hidden_size", &i), RECIPE_OK);
    EXPECT_EQ(i, 4096);
    EXPECT_EQ(recipe_get_int(r, "num_hidden_layers", &i), RECIPE_OK);
    EXPECT_EQ(i, 32);
    EXPECT_EQ(recipe_get_int(r, "neg", &i), RECIPE_OK);
    EXPECT_EQ(i, -7);

    double d = 0;
    EXPECT_EQ(recipe_get_double(r, "rope_theta", &d), RECIPE_OK);
    EXPECT_DOUBLE_EQ(d, 10000.0);
    EXPECT_EQ(recipe_get_double(r, "rms_norm_eps", &d), RECIPE_OK);
    EXPECT_DOUBLE_EQ(d, 1e-05);

    recipe_free(r);
}

TEST(LaplaceSynthesisRecipe, TypedReadsRejectRatherThanGuess) {
    const char* json = "{\"model_type\": \"llama\", \"frac\": 1.5, \"junk\": 12}";
    recipe_t* r = recipe_parse(json, strlen(json));
    ASSERT_NE(r, nullptr);

    long long i = 99;
    double d = 99;
    // a string field is not an int
    EXPECT_EQ(recipe_get_int(r, "model_type", &i), RECIPE_ERR_TYPE);
    // 1.5 is not an integer — must not truncate to 1
    EXPECT_EQ(recipe_get_int(r, "frac", &i), RECIPE_ERR_TYPE);
    EXPECT_EQ(i, 99);  // out param untouched on failure
    // absent fields are distinguishable from malformed ones
    EXPECT_EQ(recipe_get_int(r, "not_here", &i), RECIPE_ERR_MISSING);
    EXPECT_EQ(recipe_get_double(r, "not_here", &d), RECIPE_ERR_MISSING);
    EXPECT_EQ(recipe_get_double(r, "model_type", &d), RECIPE_ERR_TYPE);
    // null guards
    EXPECT_EQ(recipe_get_int(nullptr, "junk", &i), RECIPE_ERR_NULL);
    EXPECT_EQ(recipe_get_int(r, nullptr, &i), RECIPE_ERR_NULL);
    EXPECT_EQ(recipe_get_int(r, "junk", nullptr), RECIPE_ERR_NULL);

    recipe_free(r);
}
