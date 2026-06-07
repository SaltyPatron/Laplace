# Each tree-sitter grammar = the recipe for one structured-knowledge domain (code is grammar #1;
# chess/DNA/chemistry/math/music are the same engine + another grammar). Compiled as an OBJECT lib
# and linked into laplace_core. Headers (parser.h / array.h / alloc.h) are bundled in each grammar's
# src/tree_sitter/, so the grammar's own src/ is the only include dir needed.
set(_TSG "${LAPLACE_EXTERNAL}/tree-sitter-grammars")

function(laplace_add_grammar tgt srcdir)
    set(_srcs "${srcdir}/parser.c")
    if(EXISTS "${srcdir}/scanner.c")
        list(APPEND _srcs "${srcdir}/scanner.c")
    endif()
    add_library(${tgt} OBJECT ${_srcs})
    target_include_directories(${tgt} PRIVATE "${srcdir}")
    set_target_properties(${tgt} PROPERTIES POSITION_INDEPENDENT_CODE ON)
    target_compile_options(${tgt} PRIVATE -w)   # third-party generated tables
endfunction()

laplace_add_grammar(ts_grammar_python     "${_TSG}/tree-sitter-python/src")
laplace_add_grammar(ts_grammar_c          "${_TSG}/tree-sitter-c/src")
laplace_add_grammar(ts_grammar_cpp        "${_TSG}/tree-sitter-cpp/src")
laplace_add_grammar(ts_grammar_javascript "${_TSG}/tree-sitter-javascript/src")
laplace_add_grammar(ts_grammar_rust       "${_TSG}/tree-sitter-rust/src")
laplace_add_grammar(ts_grammar_go         "${_TSG}/tree-sitter-go/src")
laplace_add_grammar(ts_grammar_c_sharp    "${_TSG}/tree-sitter-c-sharp/src")
laplace_add_grammar(ts_grammar_bash       "${_TSG}/tree-sitter-bash/src")
laplace_add_grammar(ts_grammar_json       "${_TSG}/tree-sitter-json/src")
laplace_add_grammar(ts_grammar_markdown   "${_TSG}/tree-sitter-markdown/tree-sitter-markdown/src")

set(LAPLACE_TS_GRAMMARS
    ts_grammar_python ts_grammar_c ts_grammar_cpp ts_grammar_javascript ts_grammar_rust
    ts_grammar_go ts_grammar_c_sharp ts_grammar_bash ts_grammar_json ts_grammar_markdown
    PARENT_SCOPE)
