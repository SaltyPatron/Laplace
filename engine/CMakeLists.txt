cmake_minimum_required(VERSION 3.20)
project(laplace_engine
    VERSION 0.1.0
    DESCRIPTION "Laplace substrate engine — three shared C ABI libraries"
    LANGUAGES C CXX
)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(WIN32)
    add_compile_options(-Wall -Wextra)
    set(LAPLACE_ENGINE_FP_FLAGS /fp:precise /Qimf-arch-consistency:true /Qfma-)
else()
    add_compile_options(-Wall -Wextra -Wpedantic)
    set(LAPLACE_ENGINE_FP_FLAGS -fp-model=precise -fimf-arch-consistency=true -no-fma)
endif()

# Emit CodeView debug info + a linker PDB for optimized Release native builds so a
# native crash (access violation / heap fail-fast) symbolizes to function+line instead
# of a bare module+offset. /Z7 embeds CodeView in each .obj (Ninja-parallel-safe, no
# mspdbsrv serialization); /DEBUG makes the linker emit the .pdb; /OPT:REF,ICF keep the
# release binary size. Optimization level is UNCHANGED — this only adds symbols. Guarded
# to Release/RelWithDebInfo because Debug already carries its own /Zi (mutually exclusive
# with /Z7).
if(WIN32)
    # /Z7 embeds CodeView ONLY on C translation units. Applying it to the Eigen-heavy C++
    # math (dynamics/gram_schmidt.cpp etc.) blew icx's compile memory past an internal
    # crash (ICE) under the clean parallel build. The ingest corruptor and every observed
    # native crash live in C code (intent_stage.c, content_witness_batch.c, chess) — so
    # C-only debug info covers exactly what needs symbolizing, deterministically avoiding
    # the C++ template ICE. C++ frames still resolve to function names via the linker's
    # public-symbol table; only per-line info is absent for them.
    add_compile_options($<$<AND:$<CONFIG:Release,RelWithDebInfo>,$<COMPILE_LANGUAGE:C>>:/Z7>)
    # icx forwards linker flags ONLY via /Qoption,link,<flag>; a bare /DEBUG hits the
    # compiler driver and is silently dropped (never reaches link → no PDB). /OPT:REF,ICF
    # must also be forwarded because /DEBUG otherwise disables them and bloats the binary.
    add_link_options($<$<CONFIG:Release,RelWithDebInfo>:/Qoption,link,/DEBUG>)
    add_link_options($<$<CONFIG:Release,RelWithDebInfo>:/Qoption,link,/OPT:REF>)
    add_link_options($<$<CONFIG:Release,RelWithDebInfo>:/Qoption,link,/OPT:ICF>)
endif()

set(LAPLACE_TARGET_ISA "AVX2" CACHE STRING "AVX2 (default dev) | AVX512 (deploy)")
set_property(CACHE LAPLACE_TARGET_ISA PROPERTY STRINGS AVX2 AVX512)
if(WIN32)
    if(LAPLACE_TARGET_ISA STREQUAL "AVX2")
        add_compile_options(/arch:AVX2)
    elseif(LAPLACE_TARGET_ISA STREQUAL "AVX512")
        add_compile_options(/arch:AVX512)
    endif()
    if(LAPLACE_TARGET_ISA STREQUAL "AVX512")
        add_compile_definitions(LAPLACE_MKL_CBWR_AVX512=1)
    else()
        add_compile_definitions(LAPLACE_MKL_CBWR_AVX2=1)
    endif()
else()
    if(LAPLACE_TARGET_ISA STREQUAL "AVX2")
        add_compile_options(-march=haswell)
    elseif(LAPLACE_TARGET_ISA STREQUAL "AVX512")
        add_compile_options(-march=sapphirerapids)
    endif()
    if(LAPLACE_TARGET_ISA STREQUAL "AVX512")
        add_compile_definitions(LAPLACE_MKL_CBWR_AVX512=1)
    else()
        add_compile_definitions(LAPLACE_MKL_CBWR_AVX2=1)
    endif()
endif()

set(LAPLACE_EXTERNAL "${CMAKE_CURRENT_SOURCE_DIR}/../external"
    CACHE PATH "Path to external/ submodule tree")

set(LAPLACE_UNICODE_VERSION "17.0.0"
    CACHE STRING "Unicode version pinned for substrate determinism. MUST be a stable release; never alpha/beta.")
if(NOT DEFINED LAPLACE_UCD_PATH)
    if(DEFINED ENV{LAPLACE_DATA_ROOT} AND NOT "$ENV{LAPLACE_DATA_ROOT}" STREQUAL "")
        set(_LAPLACE_DATA_ROOT "$ENV{LAPLACE_DATA_ROOT}")
    elseif(WIN32)
        set(_LAPLACE_DATA_ROOT "D:/Data/Ingest")
    else()
        set(_LAPLACE_DATA_ROOT "/vault/Data")
    endif()
    set(LAPLACE_UCD_PATH "${_LAPLACE_DATA_ROOT}/UCD/Public/UCD/latest")
endif()
set(LAPLACE_UCD_PATH "${LAPLACE_UCD_PATH}"
    CACHE PATH "Path to the UCD tree (contains ucd/, ucdxml/, uca/, etc.)")
set(LAPLACE_UCDXML_ZIP "${LAPLACE_UCD_PATH}/ucdxml/ucd.nounihan.flat.zip"
    CACHE FILEPATH "UCDXML zip (nounihan flat). 67 MB uncompressed; carries all properties the engine codegen needs.")
if(NOT EXISTS "${LAPLACE_UCDXML_ZIP}")
    message(FATAL_ERROR
        "LAPLACE_UCDXML_ZIP not found:\n"
        "  ${LAPLACE_UCDXML_ZIP}\n"
        "Override -DLAPLACE_UCDXML_ZIP=... to the path of\n"
        "ucd.nounihan.flat.zip (download from https://www.unicode.org/Public/${LAPLACE_UNICODE_VERSION}/ucdxml/).")
endif()
set(LAPLACE_UCD_CONFORMANCE_DIR "${LAPLACE_UCD_PATH}/ucd"
    CACHE PATH "Directory holding the UAX#29/UAX#15 conformance test .txt files (auxiliary/Grapheme/Word/SentenceBreakTest.txt + NormalizationTest.txt)")
if(NOT EXISTS "${LAPLACE_UCD_CONFORMANCE_DIR}/auxiliary/GraphemeBreakTest.txt")
    message(FATAL_ERROR
        "LAPLACE_UCD_CONFORMANCE_DIR/auxiliary/GraphemeBreakTest.txt missing at\n"
        "  ${LAPLACE_UCD_CONFORMANCE_DIR}\n"
        "Override -DLAPLACE_UCD_CONFORMANCE_DIR=... to the ucd/ subdir.")
endif()

set(LAPLACE_UCA_VERSION "${LAPLACE_UNICODE_VERSION}"
    CACHE STRING "UCA (collation) version. Defaults to the Unicode version; MUST be stable.")
set(LAPLACE_DUCET_FILE "${LAPLACE_UCD_PATH}/uca/allkeys.txt"
    CACHE FILEPATH "DUCET allkeys.txt for the pinned UCA version.")
if(NOT EXISTS "${LAPLACE_DUCET_FILE}")
    message(FATAL_ERROR
        "LAPLACE_DUCET_FILE not found:\n"
        "  ${LAPLACE_DUCET_FILE}\n"
        "Override -DLAPLACE_DUCET_FILE=... to allkeys.txt\n"
        "(download from https://www.unicode.org/Public/UCA/${LAPLACE_UCA_VERSION}/allkeys.txt).")
endif()
message(STATUS "Laplace UCD: version=${LAPLACE_UNICODE_VERSION} xml=${LAPLACE_UCDXML_ZIP}")
message(STATUS "Laplace UCA: version=${LAPLACE_UCA_VERSION} ducet=${LAPLACE_DUCET_FILE}")

if(NOT EXISTS "${LAPLACE_EXTERNAL}/eigen/Eigen/Dense")
    message(FATAL_ERROR
        "external/eigen submodule not initialized. Run:\n"
        "    git submodule update --init external/eigen external/spectra external/blake3 external/googletest\n")
endif()

add_library(laplace_eigen INTERFACE)
target_include_directories(laplace_eigen INTERFACE "${LAPLACE_EXTERNAL}/eigen")
target_compile_definitions(laplace_eigen INTERFACE
    EIGEN_NO_DEBUG
    EIGEN_DONT_PARALLELIZE
)

add_library(laplace_spectra INTERFACE)
target_include_directories(laplace_spectra INTERFACE "${LAPLACE_EXTERNAL}/spectra/include")
target_link_libraries(laplace_spectra INTERFACE laplace_eigen)

if(CMAKE_C_COMPILER_ID STREQUAL "IntelLLVM"
   AND CMAKE_SYSTEM_PROCESSOR MATCHES "amd64|AMD64|x86_64")
    if(NOT DEFINED BLAKE3_CFLAGS_SSE2)
        set(BLAKE3_CFLAGS_SSE2  "-msse2"   CACHE STRING "the compiler flags to enable SSE2")
    endif()
    if(NOT DEFINED BLAKE3_CFLAGS_SSE4.1)
        set(BLAKE3_CFLAGS_SSE4.1 "-msse4.1" CACHE STRING "the compiler flags to enable SSE4.1")
    endif()
    if(NOT DEFINED BLAKE3_CFLAGS_AVX2)
        set(BLAKE3_CFLAGS_AVX2  "-mavx2"    CACHE STRING "the compiler flags to enable AVX2")
    endif()
    if(NOT DEFINED BLAKE3_CFLAGS_AVX512)
        set(BLAKE3_CFLAGS_AVX512 "-mavx512f -mavx512vl" CACHE STRING "the compiler flags to enable AVX512")
    endif()
    if(NOT DEFINED BLAKE3_AMD64_ASM_SOURCES)
        set(BLAKE3_AMD64_ASM_SOURCES
            blake3_avx2_x86-64_unix.S
            blake3_avx512_x86-64_unix.S
            blake3_sse2_x86-64_unix.S
            blake3_sse41_x86-64_unix.S
            CACHE STRING "BLAKE3 amd64 .S sources")
    endif()
endif()

add_subdirectory("${LAPLACE_EXTERNAL}/blake3/c"
    "${CMAKE_BINARY_DIR}/_deps/blake3" EXCLUDE_FROM_ALL)




add_library(tree_sitter_rt STATIC "${LAPLACE_EXTERNAL}/tree-sitter/lib/src/lib.c")
target_include_directories(tree_sitter_rt
    PUBLIC  "${LAPLACE_EXTERNAL}/tree-sitter/lib/include"
    PRIVATE "${LAPLACE_EXTERNAL}/tree-sitter/lib/src")
set_target_properties(tree_sitter_rt PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_compile_options(tree_sitter_rt PRIVATE -w)

enable_testing()
include(CTest)

if(BUILD_TESTING)
    if(NOT EXISTS "${LAPLACE_EXTERNAL}/googletest/CMakeLists.txt")
        message(WARNING
            "external/googletest submodule not initialized — tests will be skipped. "
            "Run: git submodule update --init external/googletest")
        set(BUILD_TESTING OFF)
    else()
        set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
        set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
        add_subdirectory("${LAPLACE_EXTERNAL}/googletest"
            "${CMAKE_BINARY_DIR}/_deps/googletest" EXCLUDE_FROM_ALL)
        if(WIN32)
            foreach(_gt gtest gtest_main gmock gmock_main)
                if(TARGET ${_gt})
                    target_compile_options(${_gt} PRIVATE -Wno-error=character-conversion)
                endif()
            endforeach()
        endif()
        include(GoogleTest)
    endif()
endif()

add_subdirectory(core)
add_subdirectory(dynamics)
add_subdirectory(synthesis)

add_subdirectory(core/tools/ucd_tables_emit)

foreach(_laplace_engine_target IN ITEMS
        laplace_core laplace_core_static
        laplace_dynamics laplace_dynamics_static
        laplace_synthesis
        laplace_core_tests laplace_dynamics_tests laplace_synthesis_tests)
    if(TARGET ${_laplace_engine_target})
        target_compile_options(${_laplace_engine_target} PRIVATE ${LAPLACE_ENGINE_FP_FLAGS})
    endif()
endforeach()
unset(_laplace_engine_target)
