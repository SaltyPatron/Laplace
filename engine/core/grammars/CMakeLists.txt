# Each tree-sitter grammar = the recipe for one structured-knowledge domain (code is grammar #1;
# chess/DNA/chemistry/math/music are the same engine + another grammar). Compiled as an OBJECT lib
# and linked into laplace_core. Headers (parser.h / array.h / alloc.h) are bundled in each grammar's
# src/tree_sitter/, so the grammar's own src/ is the only include dir needed.
set(_TSG "${LAPLACE_EXTERNAL}/tree-sitter-grammars")

function(laplace_add_grammar tgt srcdir)
    set(_srcs "${srcdir}/parser.c")
    if(EXISTS "${srcdir}/scanner.c")
        list(APPEND _srcs "${srcdir}/scanner.c")
    elseif(EXISTS "${srcdir}/scanner.cc")
        list(APPEND _srcs "${srcdir}/scanner.cc")
    elseif(EXISTS "${srcdir}/scanner.cpp")
        list(APPEND _srcs "${srcdir}/scanner.cpp")
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
laplace_add_grammar(ts_grammar_csv        "${_TSG}/tree-sitter-csv/csv/src")
laplace_add_grammar(ts_grammar_tsv        "${_TSG}/tree-sitter-csv/tsv/src")

# --- language grammars (initialized submodules with pre-generated parser.c) ---
laplace_add_grammar(ts_grammar_typescript "${_TSG}/tree-sitter-typescript/typescript/src")
laplace_add_grammar(ts_grammar_java       "${_TSG}/tree-sitter-java/src")
laplace_add_grammar(ts_grammar_ruby       "${_TSG}/tree-sitter-ruby/src")
laplace_add_grammar(ts_grammar_julia      "${_TSG}/tree-sitter-julia/src")
laplace_add_grammar(ts_grammar_kotlin     "${_TSG}/tree-sitter-kotlin/src")
laplace_add_grammar(ts_grammar_php        "${_TSG}/tree-sitter-php/php/src")

# --- grammars whose upstreams do not commit parser.c (sql: gh-pages only; swift: never) ---
# Their generated C is vendored in generated/<name>/ — committed artifacts of a scripted
# step, like engine/core/src/generated/. No network fetch, no tree-sitter CLI, on any
# machine. Regeneration recipe (grammar bumps only): generated/README.md.
laplace_add_grammar(ts_grammar_sql   "${CMAKE_CURRENT_SOURCE_DIR}/generated/sql")
laplace_add_grammar(ts_grammar_swift "${CMAKE_CURRENT_SOURCE_DIR}/generated/swift")

# --- HPC/compute grammars ---
laplace_add_grammar(ts_grammar_cuda    "${_TSG}/tree-sitter-cuda/src")
laplace_add_grammar(ts_grammar_glsl    "${_TSG}/tree-sitter-glsl/src")
laplace_add_grammar(ts_grammar_hlsl    "${_TSG}/tree-sitter-hlsl/src")
laplace_add_grammar(ts_grammar_wgsl    "${_TSG}/tree-sitter-wgsl/src")
laplace_add_grammar(ts_grammar_fortran "${_TSG}/tree-sitter-fortran/src")
laplace_add_grammar(ts_grammar_asm     "${_TSG}/tree-sitter-asm/src")
laplace_add_grammar(ts_grammar_nasm    "${_TSG}/tree-sitter-nasm/src")
laplace_add_grammar(ts_grammar_llvm    "${_TSG}/tree-sitter-llvm/src")
laplace_add_grammar(ts_grammar_mlir    "${_TSG}/tree-sitter-mlir/src")
laplace_add_grammar(ts_grammar_cmake   "${_TSG}/tree-sitter-cmake/src")
laplace_add_grammar(ts_grammar_ispc    "${_TSG}/tree-sitter-ispc/src")
laplace_add_grammar(ts_grammar_zig     "${_TSG}/tree-sitter-zig/src")

set(LAPLACE_TS_GRAMMARS
    ts_grammar_python ts_grammar_c ts_grammar_cpp ts_grammar_javascript ts_grammar_rust
    ts_grammar_go ts_grammar_c_sharp ts_grammar_bash ts_grammar_json ts_grammar_markdown
    ts_grammar_csv ts_grammar_tsv
    ts_grammar_typescript ts_grammar_java ts_grammar_ruby ts_grammar_julia
    ts_grammar_kotlin ts_grammar_php
    ts_grammar_sql ts_grammar_swift
    ts_grammar_cuda ts_grammar_glsl ts_grammar_hlsl ts_grammar_wgsl ts_grammar_fortran
    ts_grammar_asm ts_grammar_nasm ts_grammar_llvm ts_grammar_mlir ts_grammar_cmake
    ts_grammar_ispc ts_grammar_zig
    PARENT_SCOPE)
