# laplace_pg — PostgreSQL extension + laplace_native standalone shared library
#
# Same C/C++ source files compile into two targets:
#   - laplace_pg     : PostgreSQL extension shared library (loaded by Postgres)
#   - laplace_native : standalone shared library used by managed code via P/Invoke
#
# Native service implementations live under src/<service-name>/<operation>.c
# Each service has its own folder and its own .c/.h pair per operation per the
# coding standards (one type per file, files as small as cohesion allows).

cmake_minimum_required(VERSION 3.22)

# ------------------------------------------------------------------
# Source layout — one folder per service, populated as services are
# implemented during Phase 2 (Track B). A sample stub source is
# included so CMake configuration succeeds before any service exists.
# ------------------------------------------------------------------

set(LAPLACE_PG_PUBLIC_INCLUDE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/include")

# Native source list. Each service contributes one .c file per operation per
# the coding standards (one operation per file, files as small as cohesion
# allows). New services added here as Track B work lands.
set(LAPLACE_CORE_SOURCES
  src/laplace_pg.c

  # B1 — CpuidService
  src/util/cpuid.c

  # B2 — BLAKE3HashService
  src/hash/blake3_atom.c
  src/hash/blake3_composition.c
  src/hash/blake3_edge.c

  # B3 — RleService
  src/rle/rle_encode.c
  src/rle/rle_decode.c

  # B4 — SuperFibonacciService
  src/superfib/superfib_4d.c

  # B5 — Geometry4DService (interim: POINT4D primitives only)
  src/geometry4d/point4d_ops.c

  # B6 — Geometry4DOperatorsService (interim: Frechet + Hausdorff)
  src/geometry4d/polyline4d_distances.c

  # B7 — S3DomainService
  src/geometry4d/s3_ops.c

  # B8 — QuaternionService
  src/geometry4d/quaternion.c

  # B9 — HilbertCurveService (Skilling 2003, 4D, 16-bit-per-axis)
  src/hilbert/hilbert_4d.c

  # B16 — GramSchmidtService
  src/gram_schmidt/gram_schmidt.c

  # B17 — LaplacianEigenmapService (Spectra symmetric eigensolver + S^3 project)
  src/eigenmap/laplacian_eigenmap.cpp

  # B18 — Glicko2Service (paper-faithful Glickman 2013)
  src/glicko2/glicko2_apply.c

  # B19 — TensorDecodeService (safetensors header parser)
  src/safetensors/safetensors_header.c

  # B14 — UcdLookupService (lazy inverse index over LAPLACE_CODEPOINT_TABLE)
  src/ucd/ucd_lookup.c
)

# B15 KnnExactService + B20 FftService depend on Intel oneMKL (cblas_dgemm + DFTI).
# Gated behind MKL_FOUND so builds without MKL (e.g. Linux containers without
# Intel oneAPI installed) still succeed — those services are simply unavailable
# until MKL is provisioned. Everything else (POINT4D GiST, geometry4d type
# family, Glicko-2, BLAKE3, RLE, super-fib, Laplacian eigenmap header-only Eigen)
# builds without MKL.
if(MKL_FOUND)
  list(APPEND LAPLACE_CORE_SOURCES
    src/knn/knn_exact_cosine.c
    src/fft/fft_1d.c
  )
endif()

# C3 — SQL-side function bindings (compiled into the laplace_pg PG extension
# target only; gated by LAPLACE_BUILD_PG_EXTENSION inside each file).
set(LAPLACE_SQL_BINDING_SOURCES
  src/sql_bindings/sql_hash.c
  src/sql_bindings/sql_glicko2.c
  src/sql_bindings/sql_ucd.c
  src/sql_bindings/sql_quaternion.c
  src/sql_bindings/sql_hilbert.c
  src/sql_bindings/sql_rle.c
)

# G3 — generated C tables emitted by Laplace.SeedTableGenerator. Constant
# data tables for the full 1,114,112-codepoint Unicode space + script /
# block / general-category / age / bidi / emoji / ISO 639 / UCA / name /
# decomposition registries. Linked into BOTH laplace_pg and laplace_native
# so PG-side SQL functions and managed P/Invoke both get O(1) lookups
# without round-tripping the database (substrate invariant 1: identity
# is content-addressed and constant; tables can be linked-in static data).
#
# Compiled into a separate STATIC library (laplace_generated) so the
# strict /W4 /WX flags applied to our hand-written code do NOT apply to
# the machine-generated tables (which may contain large literal arrays
# that trip MSVC warnings benign for generated content). Both targets
# link this library to acquire the symbols.
set(LAPLACE_GENERATED_SOURCES
  generated/codepoint_table.c
  generated/script_registry.c
  generated/block_registry.c
  generated/gc_registry.c
  generated/age_registry.c
  generated/bidi_registry.c
  generated/emoji_sequences.c
  generated/iso639_languages.c
  generated/uca_weights.c
  generated/codepoint_names.c
  generated/codepoint_decompositions.c
)

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/codepoint_table.c")
  # Generated .c files do `#include "laplace_pg/<name>.h"`, so the headers
  # must be reachable as laplace_pg/* on some include path. Stage them into
  # ${CMAKE_CURRENT_BINARY_DIR}/staged_inc/laplace_pg/ at configure time and
  # add staged_inc to the include path. Re-runs after regeneration pick up
  # the new headers via configure_file's content-aware copy.
  set(_staged_inc "${CMAKE_CURRENT_BINARY_DIR}/staged_inc/laplace_pg")
  file(MAKE_DIRECTORY "${_staged_inc}")
  file(GLOB _gen_headers "${CMAKE_CURRENT_SOURCE_DIR}/generated/*.h")
  foreach(_h ${_gen_headers})
    get_filename_component(_h_name "${_h}" NAME)
    configure_file("${_h}" "${_staged_inc}/${_h_name}" COPYONLY)
  endforeach()

  add_library(laplace_generated STATIC ${LAPLACE_GENERATED_SOURCES})
  set_target_properties(laplace_generated PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    C_VISIBILITY_PRESET hidden
  )
  target_include_directories(laplace_generated PUBLIC
    "${CMAKE_CURRENT_BINARY_DIR}/staged_inc"
  )
  if(MSVC)
    # Generated tables contain large constant initializer lists — relax
    # MSVC's pickier warnings for this TU only. Keep /W3 baseline.
    target_compile_options(laplace_generated PRIVATE /W3 /wd4838 /wd4756)
  endif()
  set(LAPLACE_HAVE_GENERATED_TABLES ON)
  list(LENGTH LAPLACE_GENERATED_SOURCES _gen_count)
  message(STATUS "laplace_generated: linking ${_gen_count} generated table source files (full Unicode codepoint space + registries)")
else()
  set(LAPLACE_HAVE_GENERATED_TABLES OFF)
  message(STATUS "laplace_generated: skipped (run Laplace.SeedTableGenerator to populate ext/laplace_pg/generated/)")
endif()

# GEOMETRY4D type family — PG-extension-only sources (PG headers required).
# Compiled into laplace_pg target only via LAPLACE_BUILD_PG_EXTENSION guard.
# POINT4D / LINESTRING4D / BOX4D land here; remaining subtypes (POLYGON4D,
# MULTI*, TRIANGLE4D, TIN4D, POLYHEDRALSURFACE4D, CIRCULARSTRING4D,
# COMPOUNDCURVE4D, CURVEPOLYGON4D, MULTICURVE4D, MULTISURFACE4D) land as
# their downstream consumers come online per the synthesis doc.
set(LAPLACE_GEOMETRY4D_TYPE_SOURCES
  src/types/point4d_io.c
  src/types/point4d_binary.c
  src/types/point4d_ops_sql.c
  src/types/point4d_gist.c
  src/types/linestring4d_io.c
  src/types/linestring4d_ops_sql.c
  src/types/linestring4d_gist.c
  src/types/box4d_io.c
)

# Our own include directory — strict warnings apply.
set(LAPLACE_COMMON_INCLUDE_DIRS
  ${LAPLACE_PG_PUBLIC_INCLUDE_DIR}
)
# PostgreSQL server headers are SYSTEM includes. PG's c.h uses MSVC
# extensions (zero-sized arrays = C4200) and redefines macros (C4005);
# those are not bugs in our code, so we mark the include as SYSTEM to
# suppress warnings from inside it. ICU same treatment.
# Common compile definitions
set(LAPLACE_COMMON_DEFS
  LAPLACE_VERSION_MAJOR=${PROJECT_VERSION_MAJOR}
  LAPLACE_VERSION_MINOR=${PROJECT_VERSION_MINOR}
  LAPLACE_VERSION_PATCH=${PROJECT_VERSION_PATCH}
)

# ------------------------------------------------------------------
# Target 1 — laplace_pg : PostgreSQL extension shared library
# Gated on PostgreSQL_FOUND. When PG is absent the laplace_native target
# (below) still builds, so managed-side work is unblocked.
# ------------------------------------------------------------------

if(PostgreSQL_FOUND)
  add_library(laplace_pg MODULE
    ${LAPLACE_CORE_SOURCES}
    ${LAPLACE_SQL_BINDING_SOURCES}
    ${LAPLACE_GEOMETRY4D_TYPE_SOURCES})

  set_target_properties(laplace_pg PROPERTIES
    PREFIX ""
    C_VISIBILITY_PRESET hidden
  )

  target_compile_definitions(laplace_pg PRIVATE
    ${LAPLACE_COMMON_DEFS}
    LAPLACE_BUILD_PG_EXTENSION
  )
  if(WIN32)
    # PG's port.h gates Linux-only includes (e.g. <netinet/in.h>) on the
    # legacy WIN32 macro, not the standard _WIN32 that MSVC defines.
    target_compile_definitions(laplace_pg PRIVATE WIN32)
  endif()
  if(MSVC)
    # PG-style flexible array members expand to `[0]` on MSVC (since MSVC
    # rejects C99 `[]`); MSVC then warns C4200 on its own workaround. Both
    # PG and our types use this idiom (LINESTRING4D vertices, etc.). Disable
    # only this one warning for the laplace_pg target — the strict warning
    # set still applies to everything else.
    target_compile_options(laplace_pg PRIVATE /wd4200)
  endif()

  target_include_directories(laplace_pg PRIVATE ${LAPLACE_COMMON_INCLUDE_DIRS})
  target_include_directories(laplace_pg SYSTEM PRIVATE ${PostgreSQL_INCLUDE_DIRS})
  if(WIN32)
    # PG ships Windows-port shim headers (netinet/in.h, sys/select.h, etc.)
    # under include/server/port/win32/. PG's own server build adds this dir
    # to the include path; extensions on Windows must do the same.
    foreach(_pg_inc ${PostgreSQL_INCLUDE_DIRS})
      if(EXISTS "${_pg_inc}/port/win32")
        target_include_directories(laplace_pg SYSTEM PRIVATE "${_pg_inc}/port/win32")
      endif()
      if(MSVC AND EXISTS "${_pg_inc}/port/win32_msvc")
        target_include_directories(laplace_pg SYSTEM PRIVATE "${_pg_inc}/port/win32_msvc")
      endif()
    endforeach()
  endif()
  if(ICU_FOUND)
    target_include_directories(laplace_pg SYSTEM PRIVATE ${ICU_INCLUDE_DIRS})
  endif()

  target_link_libraries(laplace_pg PRIVATE
    ${PostgreSQL_LIBRARIES}
    blake3
    laplace_strict_flags
  )
  if(LAPLACE_HAVE_GENERATED_TABLES)
    target_link_libraries(laplace_pg PRIVATE laplace_generated)
  endif()
  # B17 LaplacianEigenmapService uses Eigen + Spectra (header-only) — same
  # source compiles into both targets, so both need the include paths.
  target_include_directories(laplace_pg SYSTEM PRIVATE
    ${eigen3_SOURCE_DIR}
    ${spectra_SOURCE_DIR}/include)
  # B15 KnnExactService + B20 FftService use Intel oneMKL via cblas_dgemm
  # and DFTI respectively. MKL::MKL imported target sets include + link.
  if(MKL_FOUND)
    target_link_libraries(laplace_pg PRIVATE MKL::MKL)
  endif()
  if(WIN32)
    # CMake's FindPostgreSQL returns libpq (the client library) but PG
    # extensions on Windows additionally need the server import library
    # `postgres.lib` to resolve server-side symbols like errstart/palloc/
    # errmsg/etc. Locate it next to libpq under <PG_ROOT>/lib/.
    foreach(_pg_lib ${PostgreSQL_LIBRARY_DIRS} "${PostgreSQL_INCLUDE_DIR}/../lib")
      if(EXISTS "${_pg_lib}/postgres.lib")
        target_link_libraries(laplace_pg PRIVATE "${_pg_lib}/postgres.lib")
        break()
      endif()
    endforeach()
    # Fallback: derive from one libpq.lib path if the var-loop didn't hit.
    foreach(_pg_lib ${PostgreSQL_LIBRARIES})
      get_filename_component(_pg_lib_dir "${_pg_lib}" DIRECTORY)
      if(EXISTS "${_pg_lib_dir}/postgres.lib")
        target_link_libraries(laplace_pg PRIVATE "${_pg_lib_dir}/postgres.lib")
        break()
      endif()
    endforeach()
  endif()
  if(ICU_FOUND)
    target_link_libraries(laplace_pg PRIVATE ICU::uc ICU::i18n)
  endif()

  if(WIN32)
    # PostgreSQL on Windows requires linking the postgres.lib import library
    # produced by the server build. Honor PG_LIB_DIR / PG_INCLUDE_DIR overrides.
    if(DEFINED ENV{PG_LIB_DIR})
      target_link_directories(laplace_pg PRIVATE $ENV{PG_LIB_DIR})
    endif()
  endif()
endif()

# ------------------------------------------------------------------
# Target 2 — laplace_native : standalone shared library (P/Invoke)
# ------------------------------------------------------------------

add_library(laplace_native SHARED ${LAPLACE_CORE_SOURCES})

# Place laplace_native.dll where native-dll.targets expects it:
#   $(repo)/ext/laplace_pg/build/<Configuration>/laplace_native.dll
# CMake's MSBuild generator otherwise puts target outputs under a
# <build>/<source-relative>/<config>/ subdir, which the managed build
# wouldn't find.
set_target_properties(laplace_native PROPERTIES
  # laplace_native is the managed P/Invoke surface — every C symbol declared
  # in include/laplace_pg/*.h is intended to be callable from C# via
  # [LibraryImport]. Export them all on Windows; on POSIX we still default
  # to hidden visibility because gcc/clang export everything by default
  # otherwise. Hidden + ALL_SYMBOLS=ON tells CMake to scan exported decls
  # and emit them in a .def, but the simpler approach for a managed-facing
  # DLL is to flip visibility to default and rely on the linker.
  C_VISIBILITY_PRESET default
  WINDOWS_EXPORT_ALL_SYMBOLS ON
  RUNTIME_OUTPUT_DIRECTORY         "${CMAKE_BINARY_DIR}"
  RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_BINARY_DIR}/Debug"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/Release"
  LIBRARY_OUTPUT_DIRECTORY         "${CMAKE_BINARY_DIR}"
  LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_BINARY_DIR}/Debug"
  LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/Release"
)

target_compile_definitions(laplace_native PRIVATE
  ${LAPLACE_COMMON_DEFS}
  LAPLACE_BUILD_NATIVE_DLL
)

target_include_directories(laplace_native
  PUBLIC  ${LAPLACE_PG_PUBLIC_INCLUDE_DIR}
)
if(ICU_FOUND)
  target_include_directories(laplace_native PRIVATE ${ICU_INCLUDE_DIRS})
endif()

target_link_libraries(laplace_native PRIVATE blake3 laplace_strict_flags)
if(LAPLACE_HAVE_GENERATED_TABLES)
  target_link_libraries(laplace_native PRIVATE laplace_generated)
endif()
if(ICU_FOUND)
  target_link_libraries(laplace_native PRIVATE ICU::uc ICU::i18n)
endif()
if(MKL_FOUND)
  # B15 KnnExactService uses cblas_dgemm via Intel oneMKL.
  target_link_libraries(laplace_native PRIVATE MKL::MKL)
endif()
# B17 LaplacianEigenmapService uses Eigen + Spectra (header-only).
target_include_directories(laplace_native SYSTEM PRIVATE
  ${eigen3_SOURCE_DIR}
  ${spectra_SOURCE_DIR}/include)

# ------------------------------------------------------------------
# Install rules — for `cmake --install`
# Extension bits go to PG share/extension; native dll goes to the
# .NET output staging area (managed Directory.Build.props locates it
# at $(repo)/ext/laplace_pg/build/$(Configuration)/laplace_native.dll).
# ------------------------------------------------------------------

if(PostgreSQL_FOUND)
  install(TARGETS laplace_pg
    LIBRARY DESTINATION lib
    RUNTIME DESTINATION lib
  )

  install(FILES
    sql/laplace_pg.control
    DESTINATION share/extension
    OPTIONAL
  )

  install(DIRECTORY sql/
    DESTINATION share/extension
    FILES_MATCHING PATTERN "*.sql"
  )
endif()

# ------------------------------------------------------------------
# Tests
# ------------------------------------------------------------------

if(BUILD_TESTING)
  add_subdirectory(test)
endif()

# ------------------------------------------------------------------
# Status
# ------------------------------------------------------------------

message(STATUS "laplace_pg + laplace_native targets configured")
