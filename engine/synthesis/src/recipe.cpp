#include "laplace/synthesis/recipe.h"

#include <cstddef>

/* Real impl lands Chunk 7 Story 7.16. Stubs satisfy linkage. */

struct recipe {
    int _placeholder;
};

extern "C" recipe_t* recipe_parse(const char* json_text, size_t len) {
    (void)json_text; (void)len;
    return nullptr;
}

extern "C" const char* recipe_get_field(const recipe_t* r, const char* field_name) {
    (void)r; (void)field_name;
    return nullptr;
}

extern "C" void recipe_free(recipe_t* r) {
    delete r;
}
