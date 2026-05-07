# laplace_pg — PostgreSQL extension + laplace_native standalone shared library
#
# Same C/C++ source files compile into two targets:
#   - laplace_pg     : PostgreSQL extension shared library (loaded by Postgres)
#   - laplace_native : standalone shared library used by managed code via P/Invoke
#
# Native service implementations live under src/<service-name>/<operation>.c
# Each service has its own folder and its own .c/.h pair per operation per the
# coding standards (one type per file, files as small as cohesion allows).

cmake_minimum_required(VERSION 3.28)

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

  # B18 — Glicko2Service (paper-faithful Glickman 2013)
  src/glicko2/glicko2_apply.c
)

# Common include directories applied to both targets.
set(LAPLACE_COMMON_INCLUDE_DIRS
  ${LAPLACE_PG_PUBLIC_INCLUDE_DIR}
  ${PostgreSQL_INCLUDE_DIRS}
  ${ICU_INCLUDE_DIRS}
)

# Common compile definitions
set(LAPLACE_COMMON_DEFS
  LAPLACE_VERSION_MAJOR=${PROJECT_VERSION_MAJOR}
  LAPLACE_VERSION_MINOR=${PROJECT_VERSION_MINOR}
  LAPLACE_VERSION_PATCH=${PROJECT_VERSION_PATCH}
)

# ------------------------------------------------------------------
# Target 1 — laplace_pg : PostgreSQL extension shared library
# ------------------------------------------------------------------

add_library(laplace_pg MODULE ${LAPLACE_CORE_SOURCES})

set_target_properties(laplace_pg PROPERTIES
  PREFIX ""
  C_VISIBILITY_PRESET hidden
)

target_compile_definitions(laplace_pg PRIVATE
  ${LAPLACE_COMMON_DEFS}
  LAPLACE_BUILD_PG_EXTENSION
)

target_include_directories(laplace_pg PRIVATE ${LAPLACE_COMMON_INCLUDE_DIRS})

target_link_libraries(laplace_pg PRIVATE
  ${PostgreSQL_LIBRARIES}
  ICU::uc ICU::i18n
  blake3
)

if(WIN32)
  # PostgreSQL on Windows requires linking the postgres.lib import library
  # produced by the server build. Honor PG_LIB_DIR / PG_INCLUDE_DIR overrides.
  if(DEFINED ENV{PG_LIB_DIR})
    target_link_directories(laplace_pg PRIVATE $ENV{PG_LIB_DIR})
  endif()
endif()

# ------------------------------------------------------------------
# Target 2 — laplace_native : standalone shared library (P/Invoke)
# ------------------------------------------------------------------

add_library(laplace_native SHARED ${LAPLACE_CORE_SOURCES})

set_target_properties(laplace_native PROPERTIES
  C_VISIBILITY_PRESET hidden
  WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

target_compile_definitions(laplace_native PRIVATE
  ${LAPLACE_COMMON_DEFS}
  LAPLACE_BUILD_NATIVE_DLL
)

target_include_directories(laplace_native
  PUBLIC  ${LAPLACE_PG_PUBLIC_INCLUDE_DIR}
  PRIVATE ${ICU_INCLUDE_DIRS}
)

target_link_libraries(laplace_native PRIVATE
  ICU::uc ICU::i18n
  blake3
)

# ------------------------------------------------------------------
# Install rules — for `cmake --install`
# Extension bits go to PG share/extension; native dll goes to the
# .NET output staging area (managed Directory.Build.props locates it
# at $(repo)/ext/laplace_pg/build/$(Configuration)/laplace_native.dll).
# ------------------------------------------------------------------

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
