



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
    # UNITY merges parser+scanner into one TU when both exist (halves grammar compile jobs).
    set_target_properties(${tgt} PROPERTIES
        POSITION_INDEPENDENT_CODE ON
        UNITY_BUILD ON
        UNITY_BUILD_BATCH_SIZE 4)
    target_compile_options(${tgt} PRIVATE -w)   
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

laplace_add_grammar(ts_grammar_turtle     "${_TSG}/tree-sitter-turtle/src")
laplace_add_grammar(ts_grammar_xml        "${_TSG}/tree-sitter-xml/xml/src")


laplace_add_grammar(ts_grammar_typescript "${_TSG}/tree-sitter-typescript/typescript/src")
laplace_add_grammar(ts_grammar_java       "${_TSG}/tree-sitter-java/src")
laplace_add_grammar(ts_grammar_ruby       "${_TSG}/tree-sitter-ruby/src")
laplace_add_grammar(ts_grammar_julia      "${_TSG}/tree-sitter-julia/src")
laplace_add_grammar(ts_grammar_kotlin     "${_TSG}/tree-sitter-kotlin/src")
laplace_add_grammar(ts_grammar_php        "${_TSG}/tree-sitter-php/php/src")





laplace_add_grammar(ts_grammar_sql   "${CMAKE_CURRENT_SOURCE_DIR}/generated/sql")
laplace_add_grammar(ts_grammar_swift "${CMAKE_CURRENT_SOURCE_DIR}/generated/swift")
laplace_add_grammar(ts_grammar_pgn   "${CMAKE_CURRENT_SOURCE_DIR}/generated/pgn")


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
    ts_grammar_turtle ts_grammar_xml
    ts_grammar_typescript ts_grammar_java ts_grammar_ruby ts_grammar_julia
    ts_grammar_kotlin ts_grammar_php
    ts_grammar_sql ts_grammar_swift
    ts_grammar_cuda ts_grammar_glsl ts_grammar_hlsl ts_grammar_wgsl ts_grammar_fortran
    ts_grammar_asm ts_grammar_nasm ts_grammar_llvm ts_grammar_mlir ts_grammar_cmake
    ts_grammar_ispc ts_grammar_zig
    ts_grammar_pgn
    PARENT_SCOPE)
