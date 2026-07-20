#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct recipe recipe_t;

/* Status codes shared by the typed accessors. */
#define RECIPE_OK            0
#define RECIPE_ERR_NULL     (-1)  /* null recipe / field name / out pointer */
#define RECIPE_ERR_MISSING  (-2)  /* field absent from the recipe */
#define RECIPE_ERR_TYPE     (-3)  /* field present but not parseable as the asked type */

recipe_t* recipe_parse(const char* json_text, size_t len);

const char* recipe_get_field(const recipe_t* r, const char* field_name);

/* Typed reads over the SAME parsed recipe — so callers never re-parse the JSON in
 * their own language to get a number out of it. A field that exists but is not a
 * clean integer/real yields RECIPE_ERR_TYPE rather than a silent 0: a model that
 * declares a malformed dimension must fail loudly, not be recorded as zero. */
int recipe_get_int(const recipe_t* r, const char* field_name, long long* out_value);
int recipe_get_double(const recipe_t* r, const char* field_name, double* out_value);

void recipe_free(recipe_t* r);

#ifdef __cplusplus
}
#endif
