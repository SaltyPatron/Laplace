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
    # The managed interop consumes the public C ABI from all three libraries.
    # A fresh build must export it without relying on an old cache option.
    set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)
    add_compile_options("$<$<COMPILE_LANGUAGE:C,CXX>:-Wall;-Wextra>")
    # The engine uses standard C functions (getenv/fopen/strncpy); MSVC CRT
    # deprecations are silenced as in extension/.
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS)
elseif(CMAKE_C_COMPILER_ID STREQUAL "IntelLLVM")
    add_compile_options("$<$<COMPILE_LANGUAGE:C,CXX>:-Wall;-Wextra;-Wpedantic>")
else()
    add_compile_options("$<$<COMPILE_LANGUAGE:C,CXX>:-Wall;-Wextra;-Wpedantic>")
endif()

# Emit CodeView debug info + a linker PDB for optimized Release native builds so a
# native crash (access violation / heap fail-fast) symbolizes to function+line instead
# of a bare module+offset. /Z7 embeds CodeView in each .obj (Ninja-parallel-safe, no
# mspdbsrv serialization); /DEBUG makes the linker emit the .pdb; /OPT:REF,ICF keep the
# release binary size. Optimization level is unaffected; this only adds symbols. Limited
# to Release/RelWithDebInfo because Debug carries /Zi, which is exclusive with /Z7.
if(WIN32)
    # /Z7 applies to C translation units only; on the Eigen-heavy C++ translation units
    # it exhausts icx compile memory. C++ frames still resolve to function names through
    # the linker's public-symbol table, without per-line info.
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
if(LAPLACE_TARGET_ISA STREQUAL "AVX512")
    add_compile_definitions(LAPLACE_MKL_CBWR_AVX512=1)
else()
    add_compile_definitions(LAPLACE_MKL_CBWR_AVX2=1)
endif()

if(NOT DEFINED LAPLACE_EXTERNAL OR LAPLACE_EXTERNAL STREQUAL "")
    if(DEFINED ENV{LAPLACE_EXTERNAL} AND NOT "$ENV{LAPLACE_EXTERNAL}" STREQUAL "")
        set(LAPLACE_EXTERNAL "$ENV{LAPLACE_EXTERNAL}")
    elseif(DEFINED ENV{LAPLACE_SUBMODULE_CACHE} AND NOT "$ENV{LAPLACE_SUBMODULE_CACHE}" STREQUAL "")
        set(LAPLACE_EXTERNAL "$ENV{LAPLACE_SUBMODULE_CACHE}")
    else()
        set(LAPLACE_EXTERNAL "${CMAKE_CURRENT_SOURCE_DIR}/../external")
    endif()
endif()
set(LAPLACE_EXTERNAL "${LAPLACE_EXTERNAL}"
    CACHE PATH "Path to the shared external dependency tree")

# Laplace-Native: the one implementation of identity (Laplace-Wiki Storage/Identity.md). A composition's ID is
# Laplace-Native's lp_id_compose, compiled from its checkout into laplace_core, never a copy of it (Laplace#1885).
# LAPLACE_NATIVE (laplace.env's, the desktop's machine environment) names the checkout; else the external tree's link.
if(NOT DEFINED LAPLACE_NATIVE OR LAPLACE_NATIVE STREQUAL "")
    if(DEFINED ENV{LAPLACE_NATIVE} AND NOT "$ENV{LAPLACE_NATIVE}" STREQUAL "")
        file(TO_CMAKE_PATH "$ENV{LAPLACE_NATIVE}" LAPLACE_NATIVE)
    else()
        set(LAPLACE_NATIVE "${LAPLACE_EXTERNAL}/laplace-native")
    endif()
endif()
set(LAPLACE_NATIVE "${LAPLACE_NATIVE}" CACHE PATH "Laplace-Native checkout: identity (lp_id_compose)")
if(NOT EXISTS "${LAPLACE_NATIVE}/src/identity.c" OR NOT EXISTS "${LAPLACE_NATIVE}/include/laplace/laplace.h")
    message(FATAL_ERROR "Laplace-Native not found at '${LAPLACE_NATIVE}' (src/identity.c, include/laplace/laplace.h). "
                        "Set LAPLACE_NATIVE to a Laplace-Native checkout, or link it as \${LAPLACE_EXTERNAL}/laplace-native.")
endif()

set(LAPLACE_UNICODE_VERSION "17.0.0"
    CACHE STRING "Unicode version pinned for substrate determinism. MUST be a stable release; never alpha/beta.")
if(NOT DEFINED LAPLACE_UCD_PATH)
    if(DEFINED ENV{LAPLACE_UCD_ROOT} AND NOT "$ENV{LAPLACE_UCD_ROOT}" STREQUAL "")
        set(LAPLACE_UCD_PATH "$ENV{LAPLACE_UCD_ROOT}")
    elseif(DEFINED ENV{INGEST} AND NOT "$ENV{INGEST}" STREQUAL "")
        set(LAPLACE_UCD_PATH "$ENV{INGEST}/UCD/Public/UCD/latest")
    elseif(WIN32)
        set(LAPLACE_UCD_PATH "D:/Data/Ingest/UCD/Public/UCD/latest")
    else()
        set(LAPLACE_UCD_PATH "/vault/Data/UCD/Public/UCD/latest")
    endif()
endif()
set(LAPLACE_UCD_PATH "${LAPLACE_UCD_PATH}"
    CACHE PATH "Path to the UCD tree (contains ucd/, ucdxml/, uca/, etc.)")
set(LAPLACE_UCD_RELEASE_README "${LAPLACE_UCD_PATH}/ReadMe.txt")
if(NOT EXISTS "${LAPLACE_UCD_RELEASE_README}")
    message(FATAL_ERROR
        "UCD release identity missing: ${LAPLACE_UCD_RELEASE_README}\n"
        "Laplace requires the numbered release ReadMe even when LAPLACE_UCD_PATH uses a local 'latest' alias.")
endif()
file(READ "${LAPLACE_UCD_RELEASE_README}" _laplace_ucd_release_readme LIMIT 4096)
string(FIND "${_laplace_ucd_release_readme}"
    "for Version ${LAPLACE_UNICODE_VERSION} of the Unicode Standard."
    _laplace_ucd_release_match)
if(_laplace_ucd_release_match EQUAL -1)
    message(FATAL_ERROR
        "UCD release mismatch: LAPLACE_UNICODE_VERSION=${LAPLACE_UNICODE_VERSION}\n"
        "but ${LAPLACE_UCD_RELEASE_README} does not declare that release.\n"
        "Do not let a moving 'latest' alias change content identity.")
endif()
set(LAPLACE_UCDXML_ZIP "${LAPLACE_UCD_PATH}/ucdxml/ucd.all.grouped.zip"
    CACHE FILEPATH "Complete UCDXML grouped zip (CJK + non-CJK); canonical Unicode property authority for engine codegen.")
if(NOT EXISTS "${LAPLACE_UCDXML_ZIP}")
    message(FATAL_ERROR
        "LAPLACE_UCDXML_ZIP not found:\n"
        "  ${LAPLACE_UCDXML_ZIP}\n"
        "Override -DLAPLACE_UCDXML_ZIP=... to the path of\n"
        "ucd.all.grouped.zip (download from https://www.unicode.org/Public/${LAPLACE_UNICODE_VERSION}/ucdxml/).")
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
    # External dependencies come from the PINS.tsv-driven cache, not submodules.
    # LAPLACE_EXTERNAL is a CACHE PATH, so an environment variable does not override
    # it in an existing build directory; the message names the path it checked and
    # how to repoint it.
    message(FATAL_ERROR
        "Eigen not found under LAPLACE_EXTERNAL.\n"
        "  looked for: ${LAPLACE_EXTERNAL}/eigen/Eigen/Dense\n"
        "  LAPLACE_EXTERNAL = ${LAPLACE_EXTERNAL}"
        " (CACHE PATH -- an env var will NOT override it in an existing build dir)\n"
        "\n"
        "  If that path is stale, repoint the cache:\n"
        "      cmake -S . -B build -DLAPLACE_EXTERNAL=/build/external\n"
        "\n"
        "  If the cache is absent, provision it (PINS.tsv-driven, not submodules):\n"
        "      sudo bash scripts/setup-host.sh\n")
endif()

# The Syzygy prober is provisioned like every external dependency (sync-external.sh
# into $LAPLACE_EXTERNAL, build-system-deps.sh), so its absence is a provisioning
# failure and configuration stops rather than building without the prober.
if(NOT EXISTS "${LAPLACE_EXTERNAL}/fathom/src/tbprobe.c")
    message(FATAL_ERROR
        "external/fathom not present in ${LAPLACE_EXTERNAL}. Run:\n"
        "    git submodule update --init external/fathom   (dev checkout)\n"
        "    scripts/sync-external.sh                      (provisioned prefix)\n")
endif()

add_library(laplace_eigen INTERFACE)
target_include_directories(laplace_eigen SYSTEM INTERFACE "${LAPLACE_EXTERNAL}/eigen")
target_compile_definitions(laplace_eigen INTERFACE
    EIGEN_NO_DEBUG
    EIGEN_DONT_PARALLELIZE
)

add_library(laplace_spectra INTERFACE)
target_include_directories(laplace_spectra SYSTEM INTERFACE "${LAPLACE_EXTERNAL}/spectra/include")
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

# Vendored third-party code compiles without warnings. CMAKE_WARN_DEPRECATED is off
# around the add_subdirectory so blake3's pre-3.10 cmake_minimum_required does not warn;
# it must be the CACHE entry because cmake_minimum_required ignores the directory-scope
# variable.
set(CMAKE_WARN_DEPRECATED OFF CACHE BOOL "" FORCE)
# Native owns identity, text boundaries, scalar-cache mapping and BLAKE3.
# Build its actual targets rather than compiling a private subset of its files.
add_subdirectory("${LAPLACE_NATIVE}" "${CMAKE_BINARY_DIR}/native" EXCLUDE_FROM_ALL)
if(NOT TARGET laplace_text)
    message(FATAL_ERROR "Native text decomposition requires the provisioned ICU 78 installation (LAPLACE_ICU_DIR)")
endif()
set(CMAKE_WARN_DEPRECATED ON CACHE BOOL "" FORCE)

# Consume the same Operations floating-point contract as Native and CORE-MATH.
# Intel arch-consistency intrinsics require the vendor libm that this contract excludes.
laplace_fp_flags(LAPLACE_ENGINE_FP_FLAGS LAPLACE_ENGINE_FP_LINK_FLAGS)
target_compile_options(blake3 PRIVATE "$<$<COMPILE_LANGUAGE:C,CXX>:-w>")




# Vendored Fathom Syzygy WDL/DTZ prober (MIT; pin c9c6fef0dddc05d2e242c183acf5833149ab676d,
# recorded in engine/core/src/syzygy.c). A static library compiled without warnings.
add_library(fathom_tb STATIC "${LAPLACE_EXTERNAL}/fathom/src/tbprobe.c")
target_include_directories(fathom_tb SYSTEM PUBLIC "${LAPLACE_EXTERNAL}/fathom/src")
set_target_properties(fathom_tb PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_compile_options(fathom_tb PRIVATE -w)

add_library(tree_sitter_rt STATIC "${LAPLACE_EXTERNAL}/tree-sitter/lib/src/lib.c")
target_include_directories(tree_sitter_rt
    PUBLIC  "${LAPLACE_EXTERNAL}/tree-sitter/lib/include"
    PRIVATE "${LAPLACE_EXTERNAL}/tree-sitter/lib/src")
set_target_properties(tree_sitter_rt PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_compile_options(tree_sitter_rt PRIVATE -w)

enable_testing()
include(CTest)

if(BUILD_TESTING)
    set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
    set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
    if(EXISTS "${LAPLACE_EXTERNAL}/googletest/CMakeLists.txt")
        add_subdirectory("${LAPLACE_EXTERNAL}/googletest"
            "${CMAKE_BINARY_DIR}/_deps/googletest" EXCLUDE_FROM_ALL)
    else()
        if(EXISTS "${LAPLACE_EXTERNAL}/googletest")
            message(FATAL_ERROR "Incomplete GoogleTest source: ${LAPLACE_EXTERNAL}/googletest")
        endif()
        include(FetchContent)
        FetchContent_Declare(googletest
            GIT_REPOSITORY https://github.com/google/googletest.git
            GIT_TAG 063de7e9578f82b369302001269680b4b1553359 # v1.18.0
            SOURCE_DIR "${LAPLACE_EXTERNAL}/googletest"
            BINARY_DIR "${CMAKE_BINARY_DIR}/_deps/googletest"
            EXCLUDE_FROM_ALL)
        FetchContent_MakeAvailable(googletest)
    endif()
    if(WIN32)
        # Vendored third-party: gtest's own TUs compile its headers non-SYSTEM,
        # so its internal warnings (e.g. -Wcharacter-conversion) surface here.
        foreach(_gt gtest gtest_main gmock gmock_main)
            if(TARGET ${_gt})
                target_compile_options(${_gt} PRIVATE -w)
            endif()
        endforeach()
    endif()
    include(GoogleTest)
endif()

add_subdirectory(core)
add_subdirectory(dynamics)
add_subdirectory(synthesis)


foreach(_laplace_engine_target IN ITEMS
        laplace_core laplace_core_static
        laplace_syzygy laplace_dynamics laplace_dynamics_static laplace_dynamics_pg_static
        laplace_synthesis
        laplace_core_tests laplace_dynamics_tests laplace_synthesis_tests)
    if(TARGET ${_laplace_engine_target})
        target_compile_options(${_laplace_engine_target} PRIVATE ${LAPLACE_ENGINE_FP_FLAGS})
        # The shared contract includes a portable baseline. Apply the explicitly
        # selected engine ISA after it so intrinsic-based kernels retain their ISA.
        if(WIN32)
            if(LAPLACE_TARGET_ISA STREQUAL "AVX512")
                target_compile_options(${_laplace_engine_target} PRIVATE /clang:-march=sapphirerapids)
            else()
                target_compile_options(${_laplace_engine_target} PRIVATE /clang:-march=haswell)
            endif()
        elseif(LAPLACE_TARGET_ISA STREQUAL "AVX512")
            target_compile_options(${_laplace_engine_target} PRIVATE -march=sapphirerapids)
        else()
            target_compile_options(${_laplace_engine_target} PRIVATE -march=haswell)
        endif()
        get_target_property(_laplace_target_type ${_laplace_engine_target} TYPE)
        if(NOT _laplace_target_type STREQUAL "STATIC_LIBRARY")
            target_link_options(${_laplace_engine_target} PRIVATE ${LAPLACE_ENGINE_FP_LINK_FLAGS})
        endif()
    endif()
endforeach()
unset(_laplace_engine_target)

if(UNIX AND NOT APPLE)
    target_link_options(laplace_core PRIVATE "LINKER:-z,defs")
endif()
