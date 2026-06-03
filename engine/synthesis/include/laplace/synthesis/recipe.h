#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Recipe — parsed synthesis configuration.
 *
 * Two paths:
 *   1. Auto-extracted at model ingest (config.json → Recipe entity with
 *      typed attestations: HAS_HIDDEN_SIZE, HAS_NUM_LAYERS, USES_TOKENIZER,
 *      IS_A Architecture_<X>, ...). The default re-export (fill the source's
 *      own mold) uses this Recipe as the template.
 *   2. User custom-recipe JSON for parametric variants (overrides any field).
 *
 * Opaque type-erased handle — internally holds a parsed
 * config struct + override map. */
typedef struct recipe recipe_t;

/* Parse a recipe from JSON text. Returns NULL on parse failure. */
recipe_t* recipe_parse(const char* json_text, size_t len);

/* Lookup a recipe field by name (e.g., "hidden_size", "num_attention_heads",
 * "dtype"). Returns the JSON value as a null-terminated string for the
 * caller to interpret; lifetime tied to the recipe handle. */
const char* recipe_get_field(const recipe_t* r, const char* field_name);

void recipe_free(recipe_t* r);

#ifdef __cplusplus
}
#endif
