# cmake/toolchains/gcc-deterministic.cmake
#
# Toolchain file for the Layer-0.5 system dep chain (PROJ, GEOS, GDAL,
# PostgreSQL, PostGIS, tree-sitter runtime). Uses system gcc/g++ — NOT
# icpx — per ADR 0028 (amended by ADR 0038).
#
# Why gcc here (and not the Intel toolchain):
#
#   1. Standard practice splits "system deps via system compiler" from
#      "performance-critical inner library via vendor optimized compiler."
#      icpx earns its slot in liblaplace_dynamics (oneMKL SVD, Spectra
#      eigensolvers, AVX-512). It does not earn its slot for storage/query
#      (PG), 2D geometry (GEOS), coord transforms we don't use (PROJ —
#      SRID=0; transitive cruft), or configure-time-only metadata (GDAL).
#
#   2. PROJ's upstream CMakeLists.txt has an Intel-LLVM-gated bug at
#      lines 121-122:
#         set(CMAKE_C_FLAGS ${CMAKE_C_FLAGS} -fno-fast-math)
#         set(CMAKE_CXX_FLAGS ${CMAKE_CXX_FLAGS} -fno-fast-math)
#      The unquoted ${CMAKE_C_FLAGS} expansion turns the variable into a
#      list, which CMake's "Unix Makefiles" generator emits with a literal
#      `;` between elements. Shell then parses the makefile rule as two
#      commands separated by `;` — first runs icpx without sources, second
#      tries to execute `-fno-fast-math` as a command. The branch is gated
#      on `if (CMAKE_CXX_COMPILER_ID STREQUAL "IntelLLVM")` — only fires
#      for icx/icpx. gcc/clang go through a different path with no bug.
#
#   3. gcc compiles roughly 2-3x faster than icpx, so the dep build wall-
#      clock shrinks from ~25 min to ~10-12 min on this machine.
#
# Companion toolchain for the engine: cmake/toolchains/intel-oneapi.cmake.
# The engine CMakeLists references it for liblaplace_core / dynamics /
# synthesis (where MKL/Spectra/TBB integration matters).

# ---------------------------------------------------------------------------
# Compilers — explicit absolute paths via find_program so ExternalProject_Add
# subprocesses can't accidentally inherit CC/CXX from a polluted env.
# ---------------------------------------------------------------------------

find_program(_LAPLACE_GCC NAMES gcc REQUIRED)
find_program(_LAPLACE_GXX NAMES g++ REQUIRED)

set(CMAKE_C_COMPILER   "${_LAPLACE_GCC}" CACHE FILEPATH "C compiler (gcc)")
set(CMAKE_CXX_COMPILER "${_LAPLACE_GXX}" CACHE FILEPATH "C++ compiler (g++)")

# ---------------------------------------------------------------------------
# Target ISA → -march flag (per ADR 0030 — substrate determinism via
# host-ISA pinning).
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
# Flag initialization (STRINGS — CMAKE_<LANG>_FLAGS_INIT is read by
# Compiler/<lang>.cmake before the project's CMakeLists runs, and is
# treated as a string, not a list).
# RULES.md R7 — no -ffast-math; explicit FP contract off.
# ---------------------------------------------------------------------------

set(CMAKE_C_FLAGS_INIT   "-O3 ${_MARCH} -fno-fast-math -ffp-contract=off")
set(CMAKE_CXX_FLAGS_INIT "-O3 ${_MARCH} -fno-fast-math -ffp-contract=off")
