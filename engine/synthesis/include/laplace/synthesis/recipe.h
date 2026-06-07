#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct recipe recipe_t;

recipe_t* recipe_parse(const char* json_text, size_t len);

const char* recipe_get_field(const recipe_t* r, const char* field_name);

void recipe_free(recipe_t* r);

#ifdef __cplusplus
}
#endif
