#include <gtest/gtest.h>

#include "laplace/synthesis/recipe.h"

TEST(LaplaceSynthesisRecipe, StubParseReturnsNull) {
    const char* json = "{\"hidden_size\":1024}";
    recipe_t* r = recipe_parse(json, 20);
    EXPECT_EQ(r, nullptr);
    recipe_free(r);
}
