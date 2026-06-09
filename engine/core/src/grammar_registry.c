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
extern const TSLanguage* tree_sitter_csv(void);
/* language grammars */
extern const TSLanguage* tree_sitter_typescript(void);
extern const TSLanguage* tree_sitter_java(void);
extern const TSLanguage* tree_sitter_ruby(void);
extern const TSLanguage* tree_sitter_julia(void);
extern const TSLanguage* tree_sitter_kotlin(void);
extern const TSLanguage* tree_sitter_php(void);
/* grammars whose parser.c is generated at configure time (sql: gh-pages, swift: tree-sitter generate) */
extern const TSLanguage* tree_sitter_sql(void);
extern const TSLanguage* tree_sitter_swift(void);
/* HPC/compute grammars */
extern const TSLanguage* tree_sitter_cuda(void);
extern const TSLanguage* tree_sitter_glsl(void);
extern const TSLanguage* tree_sitter_hlsl(void);
extern const TSLanguage* tree_sitter_wgsl(void);
extern const TSLanguage* tree_sitter_fortran(void);
extern const TSLanguage* tree_sitter_asm(void);
extern const TSLanguage* tree_sitter_nasm(void);
extern const TSLanguage* tree_sitter_llvm(void);
extern const TSLanguage* tree_sitter_mlir(void);
extern const TSLanguage* tree_sitter_cmake(void);
extern const TSLanguage* tree_sitter_ispc(void);
extern const TSLanguage* tree_sitter_zig(void);

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
    {"csv",        tree_sitter_csv},
    /* language grammars */
    {"typescript", tree_sitter_typescript},
    {"java",       tree_sitter_java},
    {"ruby",       tree_sitter_ruby},
    {"julia",      tree_sitter_julia},
    {"kotlin",     tree_sitter_kotlin},
    {"php",        tree_sitter_php},
    /* generated-at-configure-time grammars */
    {"sql",        tree_sitter_sql},
    {"swift",      tree_sitter_swift},
    /* HPC/compute grammars */
    {"cuda",       tree_sitter_cuda},
    {"glsl",       tree_sitter_glsl},
    {"hlsl",       tree_sitter_hlsl},
    {"wgsl",       tree_sitter_wgsl},
    {"fortran",    tree_sitter_fortran},
    {"asm",        tree_sitter_asm},
    {"nasm",       tree_sitter_nasm},
    {"llvm",       tree_sitter_llvm},
    {"mlir",       tree_sitter_mlir},
    {"cmake",      tree_sitter_cmake},
    {"ispc",       tree_sitter_ispc},
    {"zig",        tree_sitter_zig},
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
    {"csv", "csv"},
    /* language grammars */
    {"ts", "typescript"}, {"tsx", "typescript"},
    {"java", "java"},
    {"rb", "ruby"}, {"rake", "ruby"}, {"gemspec", "ruby"},
    {"jl", "julia"},
    {"kt", "kotlin"}, {"kts", "kotlin"},
    {"php", "php"}, {"phtml", "php"},
    /* HPC/compute */
    {"cu", "cuda"}, {"cuh", "cuda"},
    {"glsl", "glsl"}, {"vert", "glsl"}, {"frag", "glsl"},
    {"comp", "glsl"}, {"geom", "glsl"}, {"tesc", "glsl"}, {"tese", "glsl"},
    {"hlsl", "hlsl"}, {"hlsli", "hlsl"},
    {"wgsl", "wgsl"},
    {"f90", "fortran"}, {"f95", "fortran"}, {"f03", "fortran"}, {"f08", "fortran"},
    {"f", "fortran"}, {"for", "fortran"},
    {"s", "asm"},
    {"nasm", "nasm"}, {"asm", "nasm"},
    {"ll", "llvm"},
    {"mlir", "mlir"},
    {"sql", "sql"}, {"ddl", "sql"}, {"dml", "sql"},
    {"swift", "swift"},
    {"cmake", "cmake"},
    {"ispc", "ispc"},
    {"zig", "zig"},
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
