cmake_minimum_required(VERSION 3.20)
project(laplace
    VERSION 0.1.0
    DESCRIPTION "Laplace — content-addressable geometric-attestation substrate"
    LANGUAGES C CXX
)

# Top-level orchestrator per ADR 0032 (unified CMake build pipeline,
# Path B — PGXS retired). Drives engine + extension builds from one
# CMake tree, reads dep sources from external/ submodules per ADR 0033.

# Top-level CTest enables `ctest` from build/ to discover all engine
# tests (per-module GoogleTest registrations via gtest_discover_tests).
enable_testing()
include(CTest)

# === PG install prefix ===
#
# Defaults to stock PG 18 at /usr/lib/postgresql/18. Override to the
# custom build when Epic B's scripts/build-pg.sh produces /opt/laplace/
# pgsql-18: cmake -B build -DLAPLACE_PG_PREFIX=/opt/laplace/pgsql-18
set(LAPLACE_PG_PREFIX "/usr/lib/postgresql/18"
    CACHE PATH "Prefix where pg_config lives (stock /usr/lib/postgresql/18 or custom /opt/laplace/pgsql-18)")

find_program(LAPLACE_PG_CONFIG pg_config
    PATHS "${LAPLACE_PG_PREFIX}/bin"
    NO_DEFAULT_PATH)
if(NOT LAPLACE_PG_CONFIG)
    message(FATAL_ERROR
        "pg_config not found at ${LAPLACE_PG_PREFIX}/bin. "
        "Set -DLAPLACE_PG_PREFIX=<path to the PG install prefix>.")
endif()

execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --includedir-server
                OUTPUT_VARIABLE LAPLACE_PG_INCLUDEDIR_SERVER
                OUTPUT_STRIP_TRAILING_WHITESPACE)
execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --pkglibdir
                OUTPUT_VARIABLE LAPLACE_PG_PKGLIBDIR
                OUTPUT_STRIP_TRAILING_WHITESPACE)
execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --sharedir
                OUTPUT_VARIABLE LAPLACE_PG_SHAREDIR
                OUTPUT_STRIP_TRAILING_WHITESPACE)
execute_process(COMMAND "${LAPLACE_PG_CONFIG}" --version
                OUTPUT_VARIABLE LAPLACE_PG_VERSION
                OUTPUT_STRIP_TRAILING_WHITESPACE)

message(STATUS "Laplace PG: ${LAPLACE_PG_VERSION}")
message(STATUS "  pg_config:        ${LAPLACE_PG_CONFIG}")
message(STATUS "  server includes:  ${LAPLACE_PG_INCLUDEDIR_SERVER}")
message(STATUS "  pkglibdir:        ${LAPLACE_PG_PKGLIBDIR}")
message(STATUS "  sharedir:         ${LAPLACE_PG_SHAREDIR}")

# === SQL preprocessor (per ADR 0034 — PostGIS-pattern .sql.in → .sql) ===
find_program(LAPLACE_SQLPP NAMES cpp gpp_
    DOC "C preprocessor used to expand extension .sql.in modules into the built .sql artifact")
if(NOT LAPLACE_SQLPP)
    message(FATAL_ERROR
        "cpp (or gpp_) not found — required for .sql.in preprocessing per ADR 0034. "
        "Install via 'apt install build-essential' (already in bootstrap_build_environment).")
endif()
message(STATUS "Laplace SQLPP: ${LAPLACE_SQLPP}")

# === Engine (3 shared libs per ADR 0024) ===
add_subdirectory(engine)

# === PG extensions (2 per ADR 0025) ===
add_subdirectory(extension)
