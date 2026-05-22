cmake_minimum_required(VERSION 3.20)
project(laplace_engine
    VERSION 0.1.0
    DESCRIPTION "Laplace substrate engine — shared C ABI library"
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
# Hot-path math must be cross-machine reproducible.
add_compile_options(
    -Wall -Wextra -Wpedantic
    -fno-fast-math
    -ffp-contract=off
)

# === Where the submodule sources live ===
# Per ADR 0028 + 0032 + the all-deps-as-submodules policy (commit 574216e),
# every direct dependency is a git submodule under ../external/.
set(LAPLACE_EXTERNAL "${CMAKE_CURRENT_SOURCE_DIR}/../external" CACHE PATH "Path to external/ submodule tree")

if(NOT EXISTS "${LAPLACE_EXTERNAL}/eigen/Eigen/Dense")
    message(FATAL_ERROR
        "external/eigen submodule not initialized. Run:\n"
        "    git submodule update --init external/eigen external/spectra external/blake3\n")
endif()

# === Dependencies — from submodules, not from apt or FetchContent ===

# --- Eigen 3.4.0 — header-only template linalg ---
# Per ADR 0030, hot paths get Eigen via templates only in liblaplace_core;
# liblaplace_dynamics later flips on EIGEN_USE_MKL_ALL to dispatch heavy
# linalg through MKL. For now (Chunk 0 / 1 work) it's pure Eigen.
add_library(laplace_eigen INTERFACE)
target_include_directories(laplace_eigen INTERFACE "${LAPLACE_EXTERNAL}/eigen")
target_compile_definitions(laplace_eigen INTERFACE
    EIGEN_NO_DEBUG           # release-build assertions off
    EIGEN_DONT_PARALLELIZE   # one engine kernel = one task; TBB schedules above
)

# --- Spectra v1.2.0 — sparse eigensolvers built on Eigen ---
# Header-only. Used by liblaplace_dynamics for Laplacian eigenmaps.
add_library(laplace_spectra INTERFACE)
target_include_directories(laplace_spectra INTERFACE "${LAPLACE_EXTERNAL}/spectra/include")
target_link_libraries(laplace_spectra INTERFACE laplace_eigen)

# --- BLAKE3 1.5.4 — content hashing (per ADR 0015) ---
# The repo includes ready-made CMakeLists in c/ subdir.
add_subdirectory("${LAPLACE_EXTERNAL}/blake3/c" "${CMAKE_BINARY_DIR}/_deps/blake3" EXCLUDE_FROM_ALL)

# === Library target (current single-lib; will split per ADR 0024 in Epic A) ===
add_library(laplace_engine SHARED
    src/laplace.cpp
)

set_target_properties(laplace_engine PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    OUTPUT_NAME laplace_engine
)

target_include_directories(laplace_engine
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

target_link_libraries(laplace_engine
    PUBLIC
        laplace_eigen
        laplace_spectra
    PRIVATE
        blake3
)

# === Tests ===
enable_testing()

add_executable(test_placeholder test/test_placeholder.cpp)
target_link_libraries(test_placeholder PRIVATE laplace_engine)
add_test(NAME placeholder COMMAND test_placeholder)

# === Install (optional; for system-wide deployment) ===
install(TARGETS laplace_engine
    LIBRARY DESTINATION lib
    PUBLIC_HEADER DESTINATION include/laplace
)
install(DIRECTORY include/laplace
    DESTINATION include
    FILES_MATCHING PATTERN "*.h"
)
