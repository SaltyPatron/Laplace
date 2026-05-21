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

# === Dependencies ===

# Eigen — system package (apt libeigen3-dev → /usr/include/eigen3)
find_package(PkgConfig REQUIRED)
pkg_check_modules(EIGEN3 REQUIRED IMPORTED_TARGET eigen3)

# Spectra — header-only, fetched at configure time, pinned to v1.2.0
# Per STANDARDS.md "Dependency sources (locked)" table.
include(FetchContent)
FetchContent_Declare(
    Spectra
    GIT_REPOSITORY https://github.com/yixuan/spectra.git
    GIT_TAG        v1.2.0
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(Spectra)

# === Library target ===
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
        PkgConfig::EIGEN3
        Spectra
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
