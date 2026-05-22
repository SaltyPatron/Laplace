# cmake/toolchains/intel-oneapi.cmake
#
# Single CMake toolchain file for the Intel oneAPI build environment used by
# Laplace's Layer-0.5 dep builds (PROJ/GEOS/GDAL via CMake; PG/PostGIS use
# autoconf and set flags via env CFLAGS/CXXFLAGS, not this toolchain).
#
# Sourced by build-*.sh CMake invocations via:
#     -DCMAKE_TOOLCHAIN_FILE=$REPO_DIR/cmake/toolchains/intel-oneapi.cmake
#
# Why this exists (replaces -DCMAKE_C_COMPILER + -DCMAKE_*_FLAGS on the
# cmake command line):
#
#   When CMAKE_<LANG>_FLAGS is set via -D on the command line, CMake stores
#   it as a CMake LIST (semicolon-separated). When the downstream project's
#   CMakeLists then appends its own flags via `string(APPEND ...)` or
#   `list(APPEND ...)`, CMake joins the segments with a literal `;`. The
#   "Unix Makefiles" generator then emits that semicolon verbatim into
#   flags.make:
#
#       CXX_FLAGS = -O3 ... -ffp-contract=off;-fno-fast-math -O3 -DNDEBUG ...
#                                            ^^^
#   In a shell, `;` is a command separator. Make's compile rule expansion
#   then runs:
#       icpx [our flags] ; -fno-fast-math [their flags] -c src.cpp -o obj.o
#   which is two commands:
#       icpx [our flags]                      → "no input files"
#       -fno-fast-math [their flags] -c ...   → "/bin/sh: -fno-fast-math: not found"
#
#   Setting flags via CMAKE_<LANG>_FLAGS_INIT in a toolchain file initializes
#   them as STRINGS before the project's own CMakeLists runs. The downstream
#   project's appends operate on a string, no list-joining occurs, no
#   semicolon survives into the makefile.

# ---------------------------------------------------------------------------
# Compilers
# ---------------------------------------------------------------------------

# Use the `latest` symlink so this file survives in-place oneAPI upgrades
# without per-version edits.
set(_ONEAPI_ROOT     "/opt/intel/oneapi" CACHE PATH "Intel oneAPI install root")
set(_ONEAPI_COMPILER "${_ONEAPI_ROOT}/compiler/latest")

if(NOT EXISTS "${_ONEAPI_COMPILER}/bin/icx")
    message(FATAL_ERROR "Intel oneAPI compiler not found at ${_ONEAPI_COMPILER}/bin/icx")
endif()

set(CMAKE_C_COMPILER   "${_ONEAPI_COMPILER}/bin/icx"   CACHE FILEPATH "C compiler")
set(CMAKE_CXX_COMPILER "${_ONEAPI_COMPILER}/bin/icpx"  CACHE FILEPATH "C++ compiler")

# ---------------------------------------------------------------------------
# Target ISA → -march flag (per ADR 0030 — substrate determinism via
# host-ISA pinning + MKL_CBWR at runtime).
# ---------------------------------------------------------------------------

set(LAPLACE_TARGET_ISA "$ENV{LAPLACE_TARGET_ISA}")
if(NOT LAPLACE_TARGET_ISA)
    set(LAPLACE_TARGET_ISA "AVX2")
endif()

if(LAPLACE_TARGET_ISA STREQUAL "AVX2")
    set(_MARCH "-march=haswell")
elseif(LAPLACE_TARGET_ISA STREQUAL "AVX512")
    set(_MARCH "-march=sapphirerapids")
elseif(LAPLACE_TARGET_ISA STREQUAL "native")
    set(_MARCH "-march=native")
else()
    message(FATAL_ERROR
        "Unknown LAPLACE_TARGET_ISA='${LAPLACE_TARGET_ISA}' "
        "(expected: AVX2, AVX512, or native)")
endif()

# ---------------------------------------------------------------------------
# Flag initialization (STRINGS, not lists — that's the whole point)
#
# CMAKE_<LANG>_FLAGS_INIT is read by CMake's Compiler/<lang> modules to
# initialize CMAKE_<LANG>_FLAGS before the project CMakeLists runs.
# RULES.md R7 — no -ffast-math; explicit FP contract off; determinism by
# construction.
# ---------------------------------------------------------------------------

set(CMAKE_C_FLAGS_INIT   "-O3 ${_MARCH} -fno-fast-math -ffp-contract=off")
set(CMAKE_CXX_FLAGS_INIT "-O3 ${_MARCH} -fno-fast-math -ffp-contract=off")

# ---------------------------------------------------------------------------
# CMake-driven discovery of MKL / TBB (oneAPI ships CMake config packages).
# ---------------------------------------------------------------------------

list(APPEND CMAKE_PREFIX_PATH
    "${_ONEAPI_ROOT}/mkl/latest/lib/cmake"
    "${_ONEAPI_ROOT}/tbb/latest/lib/cmake"
)
