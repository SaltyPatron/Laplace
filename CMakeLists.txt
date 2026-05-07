cmake_minimum_required(VERSION 3.28)

project(Laplace
  VERSION 0.1.0
  DESCRIPTION "Universal digital content substrate — native compute layer"
  LANGUAGES C CXX
)

# ==========================================================================
# Global settings
# ==========================================================================

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

# Strict warnings as errors per coding standards
if(MSVC)
  add_compile_options(/W4 /WX /permissive- /utf-8)
  add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
else()
  add_compile_options(-Wall -Wextra -Wpedantic -Werror)
endif()

# ==========================================================================
# Sanitizer support (opt-in via -DLAPLACE_ASAN=ON)
# ==========================================================================

option(LAPLACE_ASAN "Enable AddressSanitizer for native builds" OFF)
if(LAPLACE_ASAN)
  if(MSVC)
    add_compile_options(/fsanitize=address)
  else()
    add_compile_options(-fsanitize=address -fno-omit-frame-pointer)
    add_link_options(-fsanitize=address)
  endif()
  message(STATUS "AddressSanitizer enabled")
endif()

# ==========================================================================
# Dependencies
# ==========================================================================

include(FetchContent)
set(FETCHCONTENT_QUIET OFF)

# BLAKE3 — content-addressing kernel
FetchContent_Declare(blake3
  GIT_REPOSITORY https://github.com/BLAKE3-team/BLAKE3.git
  GIT_TAG        1.5.5
  GIT_SHALLOW    TRUE
  SOURCE_SUBDIR  c
)
FetchContent_MakeAvailable(blake3)

# Eigen — linear algebra API used by Spectra and our kernels
FetchContent_Declare(Eigen3
  GIT_REPOSITORY https://gitlab.com/libeigen/eigen.git
  GIT_TAG        3.4.0
  GIT_SHALLOW    TRUE
)
set(EIGEN_BUILD_DOC OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(Eigen3)

# Spectra — large-scale eigenvalue problems on top of Eigen
FetchContent_Declare(Spectra
  GIT_REPOSITORY https://github.com/yixuan/spectra.git
  GIT_TAG        v1.0.1
  GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(Spectra)

# Catch2 — native test framework, used via CTest
FetchContent_Declare(Catch2
  GIT_REPOSITORY https://github.com/catchorg/Catch2.git
  GIT_TAG        v3.7.1
  GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(Catch2)
list(APPEND CMAKE_MODULE_PATH ${catch2_SOURCE_DIR}/extras)

# ICU — UAX29 segmentation and Unicode normalization
find_package(ICU COMPONENTS uc i18n REQUIRED)

# Intel MKL via oneAPI — BLAS / LAPACK / FFT / VML / VSL
find_package(MKL CONFIG QUIET)
if(NOT MKL_FOUND)
  message(WARNING
    "Intel oneMKL not found via find_package(MKL CONFIG). "
    "Either install Intel oneAPI Base Toolkit or set MKL_DIR. "
    "Native services that depend on MKL will fail to link until this is resolved."
  )
endif()

# Intel TBB — threading building blocks
find_package(TBB QUIET)
if(NOT TBB_FOUND)
  message(WARNING "Intel TBB not found; parallelism in native services will be limited.")
endif()

# PostgreSQL — required for the laplace_pg extension
find_package(PostgreSQL REQUIRED)
message(STATUS "PostgreSQL include: ${PostgreSQL_INCLUDE_DIRS}")
message(STATUS "PostgreSQL libraries: ${PostgreSQL_LIBRARIES}")

# Optional: CGAL for 4D Voronoi tessellation
find_package(CGAL CONFIG QUIET)
if(NOT CGAL_FOUND)
  message(WARNING "CGAL not found; Voronoi4DService will be unavailable until installed.")
endif()

# Optional: Intel IPP for image/signal primitives
# Optional: FFmpeg for audio/video decode
# (Both detected at the per-service CMakeLists level once those services are added.)

# ==========================================================================
# Subdirectories
# ==========================================================================

enable_testing()
include(CTest)

add_subdirectory(ext/laplace_pg)

# ==========================================================================
# Status summary
# ==========================================================================

message(STATUS "")
message(STATUS "Laplace ${PROJECT_VERSION} configured")
message(STATUS "  Build type:        ${CMAKE_BUILD_TYPE}")
message(STATUS "  C standard:        ${CMAKE_C_STANDARD}")
message(STATUS "  C++ standard:      ${CMAKE_CXX_STANDARD}")
message(STATUS "  AddressSanitizer:  ${LAPLACE_ASAN}")
message(STATUS "  PostgreSQL:        ${PostgreSQL_VERSION_STRING}")
message(STATUS "  ICU:               ${ICU_VERSION}")
message(STATUS "  MKL:               ${MKL_FOUND}")
message(STATUS "  TBB:               ${TBB_FOUND}")
message(STATUS "  CGAL:              ${CGAL_FOUND}")
message(STATUS "")
