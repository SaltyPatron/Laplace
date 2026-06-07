#include "laplace/core/grammar_registry.h"

#include <string.h>

/* Each grammar (a structured-knowledge modality recipe) is linked in as an OBJECT
 * lib and exports const TSLanguage* tree_sitter_<name>(void). */
extern const TSLanguage* tree_sitter_python(void);
extern const TSLanguage* tree_sitter_c(void);
extern const TSLanguage* tree_sitter_cpp(void);
extern const TSLanguage* tree_sitter_javascript(void);
extern const TSLanguage* tree_sitter_rust(void);
extern const TSLanguage* tree_sitter_go(void);
extern const TSLanguage* tree_sitter_c_sharp(void);
extern const TSLanguage* tree_sitter_bash(void);
extern const TSLanguage* tree_sitter_json(void);
extern const TSLanguage* tree_sitter_markdown(void);

typedef const TSLanguage* (*ts_lang_fn)(void);

typedef struct { const char* id; ts_lang_fn fn; } grammar_entry_t;

static const grammar_entry_t GRAMMARS[] = {
    {"python",     tree_sitter_python},
    {"c",          tree_sitter_c},
    {"cpp",        tree_sitter_cpp},
    {"javascript", tree_sitter_javascript},
    {"rust",       tree_sitter_rust},
    {"go",         tree_sitter_go},
    {"c-sharp",    tree_sitter_c_sharp},
    {"bash",       tree_sitter_bash},
    {"json",       tree_sitter_json},
    {"markdown",   tree_sitter_markdown},
};
static const size_t GRAMMAR_COUNT = sizeof(GRAMMARS) / sizeof(GRAMMARS[0]);

typedef struct { const char* ext; const char* id; } ext_entry_t;

static const ext_entry_t EXTS[] = {
    {"py", "python"},
    {"c", "c"}, {"h", "c"},
    {"cpp", "cpp"}, {"cc", "cpp"}, {"cxx", "cpp"}, {"hpp", "cpp"}, {"hh", "cpp"},
    {"js", "javascript"}, {"mjs", "javascript"}, {"cjs", "javascript"},
    {"rs", "rust"},
    {"go", "go"},
    {"cs", "c-sharp"},
    {"sh", "bash"}, {"bash", "bash"},
    {"json", "json"},
    {"md", "markdown"}, {"markdown", "markdown"},
};
static const size_t EXT_COUNT = sizeof(EXTS) / sizeof(EXTS[0]);

const TSLanguage* laplace_grammar_lookup_by_id(const char* modality_id) {
    if (!modality_id) return NULL;
    for (size_t i = 0; i < GRAMMAR_COUNT; ++i)
        if (strcmp(GRAMMARS[i].id, modality_id) == 0)
            return GRAMMARS[i].fn();
    return NULL;
}

const TSLanguage* laplace_grammar_lookup_by_ext(const char* ext) {
    if (!ext) return NULL;
    for (size_t i = 0; i < EXT_COUNT; ++i)
        if (strcmp(EXTS[i].ext, ext) == 0)
            return laplace_grammar_lookup_by_id(EXTS[i].id);
    return NULL;
}

size_t laplace_grammar_list(const char** out, size_t cap) {
    if (out)
        for (size_t i = 0; i < GRAMMAR_COUNT && i < cap; ++i)
            out[i] = GRAMMARS[i].id;
    return GRAMMAR_COUNT;
}
