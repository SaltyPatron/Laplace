cmake_minimum_required(VERSION 3.20)
project(laplace_engine
    VERSION 0.1.0
    DESCRIPTION "Laplace substrate engine — three shared C ABI libraries (ADR 0024)"
    LANGUAGES C CXX
)

# === Language standards ===
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# === Determinism discipline (RULES.md R7) ===
# NO -ffast-math, NO fp-contract fusion on hot paths.
add_compile_options(
    -Wall -Wextra -Wpedantic
    -fno-fast-math
    -ffp-contract=off
)

# === ISA targeting (per ADR 0030) ===
set(LAPLACE_TARGET_ISA "AVX2" CACHE STRING "AVX2 (default dev) | AVX512 (deploy)")
set_property(CACHE LAPLACE_TARGET_ISA PROPERTY STRINGS AVX2 AVX512)
if(LAPLACE_TARGET_ISA STREQUAL "AVX2")
    add_compile_options(-march=haswell)
elseif(LAPLACE_TARGET_ISA STREQUAL "AVX512")
    add_compile_options(-march=sapphirerapids)
endif()

# === Submodule external/ tree (per ADR 0033) ===
set(LAPLACE_EXTERNAL "${CMAKE_CURRENT_SOURCE_DIR}/../external"
    CACHE PATH "Path to external/ submodule tree")

if(NOT EXISTS "${LAPLACE_EXTERNAL}/eigen/Eigen/Dense")
    message(FATAL_ERROR
        "external/eigen submodule not initialized. Run:\n"
        "    git submodule update --init external/eigen external/spectra external/blake3 external/googletest\n")
endif()

# === Header-only deps via INTERFACE libraries (per ADR 0030 amended by 0033) ===

# Eigen 3.4.0 — header-only template linalg
add_library(laplace_eigen INTERFACE)
target_include_directories(laplace_eigen INTERFACE "${LAPLACE_EXTERNAL}/eigen")
target_compile_definitions(laplace_eigen INTERFACE
    EIGEN_NO_DEBUG
    EIGEN_DONT_PARALLELIZE   # one engine kernel = one task; TBB schedules above
)

# Spectra v1.2.0 — sparse eigensolvers on Eigen
add_library(laplace_spectra INTERFACE)
target_include_directories(laplace_spectra INTERFACE "${LAPLACE_EXTERNAL}/spectra/include")
target_link_libraries(laplace_spectra INTERFACE laplace_eigen)

# BLAKE3 1.5.4 — content hashing (per ADR 0015)
add_subdirectory("${LAPLACE_EXTERNAL}/blake3/c"
    "${CMAKE_BINARY_DIR}/_deps/blake3" EXCLUDE_FROM_ALL)

# === Testing (GoogleTest via submodule per ADR 0033 + STANDARDS.md) ===
enable_testing()
include(CTest)

# Only configure GoogleTest if we're actually building tests
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
        include(GoogleTest)
    endif()
endif()

# === Engine module split (per ADR 0024) ===
# Three shared libraries; each subdir owns its own CMakeLists.

add_subdirectory(core)
add_subdirectory(dynamics)
add_subdirectory(synthesis)
