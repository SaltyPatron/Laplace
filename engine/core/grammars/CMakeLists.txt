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

# --- language grammars (initialized submodules with pre-generated parser.c) ---
laplace_add_grammar(ts_grammar_typescript "${_TSG}/tree-sitter-typescript/typescript/src")
laplace_add_grammar(ts_grammar_java       "${_TSG}/tree-sitter-java/src")
laplace_add_grammar(ts_grammar_ruby       "${_TSG}/tree-sitter-ruby/src")
laplace_add_grammar(ts_grammar_julia      "${_TSG}/tree-sitter-julia/src")
laplace_add_grammar(ts_grammar_kotlin     "${_TSG}/tree-sitter-kotlin/src")
laplace_add_grammar(ts_grammar_php        "${_TSG}/tree-sitter-php/php/src")

# --- grammars requiring parser.c generation at configure time ---
# Idempotent: the execute_process blocks are no-ops when parser.c already exists.
# On a fresh clone both will regenerate automatically before the build.

# SQL: parser.c lives on gh-pages branch, not main.
set(_SQL_SRC "${_TSG}/tree-sitter-sql/src")
if(NOT EXISTS "${_SQL_SRC}/parser.c")
    message(STATUS "tree-sitter-sql: fetching parser.c from origin/gh-pages")
    execute_process(COMMAND git fetch --depth=1 origin gh-pages
        WORKING_DIRECTORY "${_TSG}/tree-sitter-sql" OUTPUT_QUIET ERROR_QUIET)
    execute_process(COMMAND git checkout origin/gh-pages -- src/parser.c src/scanner.c src/tree_sitter/
        WORKING_DIRECTORY "${_TSG}/tree-sitter-sql" RESULT_VARIABLE _rc)
    if(NOT _rc EQUAL 0)
        message(FATAL_ERROR "tree-sitter-sql: failed to checkout gh-pages src/ (rc=${_rc}). "
            "Ensure git can reach github.com or pre-generate with: "
            "cd ${_TSG}/tree-sitter-sql && git fetch origin gh-pages && "
            "git checkout origin/gh-pages -- src/parser.c src/scanner.c src/tree_sitter/")
    endif()
endif()
laplace_add_grammar(ts_grammar_sql "${_SQL_SRC}")

# Swift: no pre-generated parser.c; requires tree-sitter CLI.
set(_SWIFT_SRC "${_TSG}/tree-sitter-swift/src")
if(NOT EXISTS "${_SWIFT_SRC}/parser.c")
    message(STATUS "tree-sitter-swift: running tree-sitter generate")
    execute_process(COMMAND tree-sitter generate
        WORKING_DIRECTORY "${_TSG}/tree-sitter-swift" RESULT_VARIABLE _rc)
    if(NOT _rc EQUAL 0)
        message(FATAL_ERROR "tree-sitter-swift: tree-sitter generate failed (rc=${_rc}). "
            "Install tree-sitter-cli: npm install -g tree-sitter-cli")
    endif()
endif()
laplace_add_grammar(ts_grammar_swift "${_SWIFT_SRC}")

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
    ts_grammar_csv
    ts_grammar_typescript ts_grammar_java ts_grammar_ruby ts_grammar_julia
    ts_grammar_kotlin ts_grammar_php
    ts_grammar_sql ts_grammar_swift
    ts_grammar_cuda ts_grammar_glsl ts_grammar_hlsl ts_grammar_wgsl ts_grammar_fortran
    ts_grammar_asm ts_grammar_nasm ts_grammar_llvm ts_grammar_mlir ts_grammar_cmake
    ts_grammar_ispc ts_grammar_zig
    PARENT_SCOPE)
