cmake_minimum_required(VERSION 3.22)

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

# Strict warnings as errors per coding standards apply to OUR targets only,
# never to FetchContent dependencies (BLAKE3, Eigen, Spectra, Catch2). Each
# project applies the right strictness for its own code.
#
# Common globally-safe defines stay at this scope; per-target compile options
# are applied via the laplace_strict_flags interface library below.
if(MSVC)
  add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

add_library(laplace_strict_flags INTERFACE)
if(MSVC)
  target_compile_options(laplace_strict_flags INTERFACE
    "$<$<COMPILE_LANGUAGE:C,CXX>:/W4>"
    "$<$<COMPILE_LANGUAGE:C,CXX>:/WX>"
    "$<$<COMPILE_LANGUAGE:C,CXX>:/permissive->"
    "$<$<COMPILE_LANGUAGE:C,CXX>:/utf-8>"
  )
else()
  target_compile_options(laplace_strict_flags INTERFACE
    "$<$<COMPILE_LANGUAGE:C,CXX>:-Wall>"
    "$<$<COMPILE_LANGUAGE:C,CXX>:-Wextra>"
    "$<$<COMPILE_LANGUAGE:C,CXX>:-Wpedantic>"
    "$<$<COMPILE_LANGUAGE:C,CXX>:-Werror>"
  )
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

# PostgreSQL — required for the laplace_pg extension target only.
# laplace_native.dll (the managed P/Invoke target) does not depend on PG;
# making detection optional lets the managed-side build proceed even when
# PG dev headers/libs are absent (e.g. on a CI runner without PG installed,
# or before the developer has set up a local PG dev environment).
#
# The laplace_pg extension target inside ext/laplace_pg is gated on
# PostgreSQL_FOUND and silently skipped when PG is unavailable.
#
# Standard Windows install (C:\Program Files\PostgreSQL\<major>) is
# auto-discovered via hint paths below. Override by exporting
# PostgreSQL_ROOT or passing -DPostgreSQL_ROOT=...
if(WIN32 AND NOT DEFINED PostgreSQL_ROOT AND NOT DEFINED ENV{PostgreSQL_ROOT})
  file(GLOB _pg_candidate_dirs LIST_DIRECTORIES TRUE
    "C:/Program Files/PostgreSQL/*"
    "D:/Program Files/PostgreSQL/*")
  foreach(_pg_dir ${_pg_candidate_dirs})
    if(IS_DIRECTORY "${_pg_dir}" AND EXISTS "${_pg_dir}/include/libpq-fe.h")
      set(PostgreSQL_ROOT "${_pg_dir}")
      message(STATUS "PostgreSQL hint path: ${PostgreSQL_ROOT}")
      break()
    endif()
  endforeach()
endif()

find_package(PostgreSQL)
if(PostgreSQL_FOUND)
  message(STATUS "PostgreSQL include:   ${PostgreSQL_INCLUDE_DIRS}")
  message(STATUS "PostgreSQL libraries: ${PostgreSQL_LIBRARIES}")
else()
  message(STATUS "PostgreSQL not found; laplace_pg extension target will be skipped. "
                 "laplace_native.dll (managed P/Invoke target) builds unconditionally.")
endif()

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
if(PostgreSQL_FOUND)
  message(STATUS "  PostgreSQL:        ${PostgreSQL_VERSION_STRING}")
else()
  message(STATUS "  PostgreSQL:        NOT FOUND (laplace_pg target skipped)")
endif()
message(STATUS "  MKL:               ${MKL_FOUND}")
message(STATUS "  TBB:               ${TBB_FOUND}")
message(STATUS "  CGAL:              ${CGAL_FOUND}")
message(STATUS "")
