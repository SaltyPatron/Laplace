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

# === Extension install destination ===
#
# By default (LAPLACE_INSTALL_STAGED=OFF), PG extensions install to the
# absolute pkglibdir/sharedir that pg_config reports — i.e. the system PG
# tree (/usr/lib/postgresql/$PG_MAJOR/lib and /usr/share/postgresql/$PG_MAJOR
# for a stock Debian/Ubuntu PG). That tree is root-owned, so installing
# requires sudo. This is the right default for CI and for installing into
# a custom /opt/laplace/pgsql-$PG_MAJOR built by `just build-deps`.
#
# When LAPLACE_INSTALL_STAGED=ON, extensions install to the PARALLEL
# layout under CMAKE_INSTALL_PREFIX — e.g. /opt/laplace/lib/postgresql/$PG_MAJOR
# and /opt/laplace/share/postgresql/$PG_MAJOR/extension. /opt/laplace is
# laplace-runner-owned (per bootstrap_build_environment), so the install is
# sudo-free. The running PG is told to look there via the
# extension_control_path + dynamic_library_path GUCs set by
# bootstrap_pg_extension_paths (one-time, in sudo just bootstrap).
#
# Derive PG major version from pg_config --version output, e.g.
# "PostgreSQL 18.3" → "18".
string(REGEX MATCH "PostgreSQL +([0-9]+)" _pg_major_match "${LAPLACE_PG_VERSION}")
set(LAPLACE_PG_MAJOR "${CMAKE_MATCH_1}")
if(NOT LAPLACE_PG_MAJOR)
    message(FATAL_ERROR
        "Could not parse PG major version from '${LAPLACE_PG_VERSION}'. "
        "Expected output of `pg_config --version` to look like 'PostgreSQL 18.X'.")
endif()

option(LAPLACE_INSTALL_STAGED
    "Install PG extensions to CMAKE_INSTALL_PREFIX/{lib,share}/postgresql/PG_MAJOR (sudo-free) instead of pg_config's absolute system paths"
    OFF)

# Derived from LAPLACE_INSTALL_STAGED — NOT cached. With CACHE PATH the
# previous configure's value sticks (subsequent set() is a no-op for
# already-cached entries), so toggling STAGED in a persistent build dir
# (e.g. CI with clean: false) would still see the stale install paths.
# These are derived knobs; the user-facing toggle is LAPLACE_INSTALL_STAGED.
if(LAPLACE_INSTALL_STAGED)
    set(LAPLACE_EXT_LIBDIR
        "${CMAKE_INSTALL_PREFIX}/lib/postgresql/${LAPLACE_PG_MAJOR}")
    set(LAPLACE_EXT_SHAREDIR
        "${CMAKE_INSTALL_PREFIX}/share/postgresql/${LAPLACE_PG_MAJOR}/extension")
else()
    set(LAPLACE_EXT_LIBDIR   "${LAPLACE_PG_PKGLIBDIR}")
    set(LAPLACE_EXT_SHAREDIR "${LAPLACE_PG_SHAREDIR}/extension")
endif()

# === Install-time RPATH ===
# CMake strips build-tree RPATH at install by default, leaving installed
# .so files unable to find their non-system deps. The engine's dynamics
# library links MKL + TBB + Intel compiler runtime (libsvml, libimf,
# libintlc). laplace_substrate.so links engine libs at /opt/laplace/lib
# (transitively pulls MKL/TBB through dynamics). Without this, PG can't
# load the extension at runtime — "libmkl_intel_lp64.so.3: cannot open
# shared object file" — unless the PG service has LD_LIBRARY_PATH set
# to include these paths, which it doesn't and shouldn't need to.
#
# Paths are derived from the oneAPI env vars set by /opt/intel/oneapi/setvars.sh
# (MKLROOT, TBBROOT, CMPLR_ROOT) so this is portable across machines —
# any oneAPI install layout (different versions, alternative prefix)
# resolves correctly as long as setvars.sh was sourced before configure.
# If setvars.sh wasn't sourced, the build wouldn't have found icpx / MKL /
# TBB in the first place, so a missing env var here means the build is
# already broken at configure — we fail loud rather than embed a guess.
#
# Build-tree RPATH (CMAKE_BUILD_RPATH) is preserved separately and lets
# ctest in build/ work without install. CMAKE_BUILD_WITH_INSTALL_RPATH
# defaults FALSE, so install rewrites RPATH from build-tree values to these.
set(CMAKE_INSTALL_RPATH "${CMAKE_INSTALL_PREFIX}/lib")
# /opt/laplace/geos/lib so laplace_geom finds libgeos_c at PG load time.
# liblwgeom.a is static-linked into laplace_geom; its lwgeom_geos_* code
# paths reference the GEOS C API at runtime. We don't want to depend on
# postgis-3.so being loaded first (RTLD_NOW resolves DT_NEEDED before
# any other extension's deps are visible).
list(APPEND CMAKE_INSTALL_RPATH "/opt/laplace/geos/lib")
list(APPEND CMAKE_INSTALL_RPATH "/opt/laplace/proj/lib")
foreach(_oneapi_var_pair IN ITEMS
        "MKLROOT;/lib"
        "TBBROOT;/lib/intel64/gcc4.8"
        "CMPLR_ROOT;/lib")
    list(GET _oneapi_var_pair 0 _var)
    list(GET _oneapi_var_pair 1 _suffix)
    if(NOT DEFINED ENV{${_var}})
        message(FATAL_ERROR
            "$ENV{${_var}} is unset. Source /opt/intel/oneapi/setvars.sh "
            "before configuring — oneAPI env vars drive both the build "
            "(icpx/MKL/TBB discovery) and the install RPATH.")
    endif()
    list(APPEND CMAKE_INSTALL_RPATH "$ENV{${_var}}${_suffix}")
endforeach()
unset(_oneapi_var_pair)
unset(_var)
unset(_suffix)

message(STATUS "Laplace extension install:")
message(STATUS "  staged:           ${LAPLACE_INSTALL_STAGED}")
message(STATUS "  install prefix:   ${CMAKE_INSTALL_PREFIX}")
message(STATUS "  ext libdir:       ${LAPLACE_EXT_LIBDIR}")
message(STATUS "  ext sharedir:     ${LAPLACE_EXT_SHAREDIR}")

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
