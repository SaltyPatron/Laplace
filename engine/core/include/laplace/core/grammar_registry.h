#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Opaque recipe handle. A grammar is the recipe for one structured-knowledge
 * modality; the registry maps a modality id / file extension to its recipe.
 * The concrete type is the grammar-execution mechanism's (tree-sitter today,
 * a Laplace executor later) — callers never see past this opaque handle. */
typedef struct TSLanguage TSLanguage;

/* Look up a modality recipe by id ("python","c","cpp","javascript","rust","go",
 * "c-sharp","bash","json","markdown") or by file extension. NULL if unknown. */
const TSLanguage* laplace_grammar_lookup_by_id(const char* modality_id);
const TSLanguage* laplace_grammar_lookup_by_ext(const char* ext);

/* Enumerate available modality ids; returns total count, fills out[] up to cap. */
size_t laplace_grammar_list(const char** out, size_t cap);

#ifdef __cplusplus
}
#endif
