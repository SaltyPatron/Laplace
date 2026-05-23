# external/CMakeLists.txt
#
# Unified build pipeline for the Layer-0.5 system dependency chain:
#   PROJ → GEOS → GDAL → PostgreSQL → PostGIS → tree-sitter (runtime only)
#
# Each dep is built via ExternalProject_Add — its build runs as a fully
# isolated subprocess in its own CMake context, so upstream bugs (e.g.,
# PROJ's IntelLLVM-only unquoted set() at CMakeLists:121-122) cannot leak
# into our outer build. Mixed upstream build systems (CMake / autoconf /
# Make) are normalized via explicit CONFIGURE / BUILD / INSTALL overrides.
#
# Toolchain: cmake/toolchains/gcc-deterministic.cmake (system gcc, not
# icpx) per ADR 0028 amended by ADR 0038. icpx is reserved for the engine
# (liblaplace_dynamics) where MKL/Spectra/AVX-512 earns its slot.
#
# Install prefix: /opt/laplace/{proj,geos,gdal,pgsql-18,tree-sitter}
# Owned by ahart:laplace-runner mode 2775 (per bootstrap-laplace-runner.sh).
# No sudo needed for builds or installs.
#
# Usage:
#   cmake -B build/deps -S external
#   cmake --build build/deps -j
#
# Or via Justfile:
#   just build-deps
#
# Idempotent: ExternalProject_Add uses stamp files; re-running with the
# same submodule SHAs is a no-op. Real work happens only when an
# external/<dep> submodule pointer changes (or a manual `cmake --build
# build/deps --target <dep>-clean-rebuild` is invoked).
#
# Per ADR 0038 — supersedes the prior per-dep shell scripts:
#   scripts/build-{proj,geos,gdal,pg,postgis,tree-sitter}.sh
#   scripts/build-all-deps.sh

cmake_minimum_required(VERSION 3.22)
project(laplace-deps LANGUAGES NONE)

include(ExternalProject)
include(ProcessorCount)

ProcessorCount(_NPROC)
if(_NPROC EQUAL 0)
    set(_NPROC 1)
endif()

set(LAPLACE_DEPS_PREFIX "/opt/laplace" CACHE PATH
    "Install prefix root for laplace system deps")
set(LAPLACE_DEPS_TOOLCHAIN
    "${CMAKE_CURRENT_LIST_DIR}/../cmake/toolchains/gcc-deterministic.cmake")

# Verify install prefix exists + is writable (set up by
# scripts/bootstrap-laplace-runner.sh). If this fails, the user has not
# yet run `sudo just bootstrap` — that's the legitimate Layer-0 surface
# per ADR 0019 + RULES.md R23.
if(NOT EXISTS "${LAPLACE_DEPS_PREFIX}")
    message(FATAL_ERROR
        "${LAPLACE_DEPS_PREFIX} does not exist. Run `sudo just bootstrap` "
        "to set it up (Layer-0; one-time per machine, per ADR 0019).")
endif()

# Compiler flag string passed to autoconf builds (PG, PostGIS) and Make
# builds (tree-sitter). For CMake-based builds (PROJ, GEOS, GDAL) the
# toolchain file's CMAKE_<LANG>_FLAGS_INIT handles this.
set(_LAPLACE_TARGET_ISA "$ENV{LAPLACE_TARGET_ISA}")
if(NOT _LAPLACE_TARGET_ISA)
    set(_LAPLACE_TARGET_ISA "AVX2")
endif()
if(_LAPLACE_TARGET_ISA STREQUAL "AVX2")
    set(_MARCH "-march=haswell")
elseif(_LAPLACE_TARGET_ISA STREQUAL "AVX512")
    set(_MARCH "-march=sapphirerapids")
elseif(_LAPLACE_TARGET_ISA STREQUAL "native")
    set(_MARCH "-march=native")
else()
    message(FATAL_ERROR
        "Unknown LAPLACE_TARGET_ISA='${_LAPLACE_TARGET_ISA}'")
endif()

set(_LAPLACE_CFLAGS "-O3 ${_MARCH} -fno-fast-math -ffp-contract=off")

# Common CMAKE_ARGS reused across the CMake-based deps (PROJ/GEOS/GDAL).
set(_COMMON_CMAKE_ARGS
    "-DCMAKE_TOOLCHAIN_FILE=${LAPLACE_DEPS_TOOLCHAIN}"
    "-DCMAKE_BUILD_TYPE=Release"
    "-DBUILD_TESTING=OFF"
    "-DBUILD_SHARED_LIBS=ON"
)

# ---------------------------------------------------------------------------
# PROJ — coordinate transformation library (CMake-based)
# ---------------------------------------------------------------------------

ExternalProject_Add(proj
    SOURCE_DIR  "${CMAKE_CURRENT_SOURCE_DIR}/proj"
    BINARY_DIR  "${CMAKE_BINARY_DIR}/proj-build"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/proj"
    CMAKE_ARGS
        ${_COMMON_CMAKE_ARGS}
        "-DCMAKE_INSTALL_PREFIX=<INSTALL_DIR>"
        "-DENABLE_CURL=ON"
        "-DENABLE_TIFF=ON"
    BUILD_COMMAND
        "${CMAKE_COMMAND}" --build <BINARY_DIR> -j ${_NPROC}
)

# ---------------------------------------------------------------------------
# GEOS — geometry engine (CMake-based)
# ---------------------------------------------------------------------------

ExternalProject_Add(geos
    SOURCE_DIR  "${CMAKE_CURRENT_SOURCE_DIR}/geos"
    BINARY_DIR  "${CMAKE_BINARY_DIR}/geos-build"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/geos"
    CMAKE_ARGS
        ${_COMMON_CMAKE_ARGS}
        "-DCMAKE_INSTALL_PREFIX=<INSTALL_DIR>"
    BUILD_COMMAND
        "${CMAKE_COMMAND}" --build <BINARY_DIR> -j ${_NPROC}
)

# ---------------------------------------------------------------------------
# GDAL — geospatial data abstraction (CMake-based, depends on PROJ)
# ---------------------------------------------------------------------------

ExternalProject_Add(gdal
    SOURCE_DIR  "${CMAKE_CURRENT_SOURCE_DIR}/gdal"
    BINARY_DIR  "${CMAKE_BINARY_DIR}/gdal-build"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/gdal"
    DEPENDS     proj
    CMAKE_ARGS
        ${_COMMON_CMAKE_ARGS}
        "-DCMAKE_INSTALL_PREFIX=<INSTALL_DIR>"
        "-DCMAKE_PREFIX_PATH=${LAPLACE_DEPS_PREFIX}/proj"
        "-DPROJ_INCLUDE_DIR=${LAPLACE_DEPS_PREFIX}/proj/include"
        "-DPROJ_LIBRARY=${LAPLACE_DEPS_PREFIX}/proj/lib/libproj.so"
        "-DGDAL_BUILD_OPTIONAL_DRIVERS=OFF"
        "-DOGR_BUILD_OPTIONAL_DRIVERS=OFF"
        "-DGDAL_USE_INTERNAL_LIBS=WHEN_NO_EXTERNAL"
    BUILD_COMMAND
        "${CMAKE_COMMAND}" --build <BINARY_DIR> -j ${_NPROC}
)

# ---------------------------------------------------------------------------
# tree-sitter runtime — Make-based, in-source build.
# (The 300+ language grammars under external/tree-sitter-grammars/ are NOT
# part of this chain — they are built per-grammar on demand by the future
# CodeDecomposer plugin, per ADR 0033.)
# ---------------------------------------------------------------------------

ExternalProject_Add(tree-sitter
    SOURCE_DIR  "${CMAKE_CURRENT_SOURCE_DIR}/tree-sitter"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/tree-sitter"
    BUILD_IN_SOURCE 1
    CONFIGURE_COMMAND ""
    BUILD_COMMAND
        make -j ${_NPROC}
        "CC=gcc"
        "CFLAGS=${_LAPLACE_CFLAGS} -fPIC"
        "PREFIX=<INSTALL_DIR>"
    INSTALL_COMMAND
        make install "PREFIX=<INSTALL_DIR>"
)

# ---------------------------------------------------------------------------
# PostgreSQL 18 — autoconf-based, VPATH out-of-source build
# ---------------------------------------------------------------------------

# `env -u PGXS` strips PGXS from the build environment. If the developer
# shell has a PG install's setup script sourced (e.g. PGXS pointed at
# /usr/lib/postgresql/18/lib/pgxs/src/makefiles/pgxs.mk), PG's own
# src/Makefile.global takes the PGXS extension-build branch — which sets
# srcdir = $(dir $(firstword $(MAKEFILE_LIST))) instead of
# $(top_srcdir)/$(subdir). The very first sub-include
# (src/backend/Makefile line 26: `include $(srcdir)/common.mk`) then
# resolves to "./common.mk" inside the BINARY_DIR (which doesn't have
# common.mk) and the build dies with "Makefile:26: common.mk: No such
# file or directory". Stripping PGXS for these invocations makes the build
# independent of the developer's shell exports.
ExternalProject_Add(postgresql
    SOURCE_DIR  "${CMAKE_CURRENT_SOURCE_DIR}/postgresql"
    BINARY_DIR  "${CMAKE_BINARY_DIR}/postgresql-build"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/pgsql-18"
    CONFIGURE_COMMAND
        env -u PGXS
        "<SOURCE_DIR>/configure"
        "CC=gcc"
        "CXX=g++"
        "CFLAGS=${_LAPLACE_CFLAGS}"
        "CXXFLAGS=${_LAPLACE_CFLAGS}"
        "--prefix=<INSTALL_DIR>"
        "--with-icu"
        "--with-ssl=openssl"
        "--with-zlib"
        "--with-uuid=e2fs"
        "--with-libxml"
        "--without-readline"
    BUILD_COMMAND   env -u PGXS make -j ${_NPROC}
    INSTALL_COMMAND env -u PGXS make install
)

# ---------------------------------------------------------------------------
# PostGIS 3.6.3 — autoconf, in-source build (does not VPATH cleanly).
# Depends on PG + GEOS + PROJ + GDAL all being installed first.
# autogen.sh runs as PATCH_COMMAND to generate ./configure from configure.ac.
# ---------------------------------------------------------------------------

ExternalProject_Add(postgis
    SOURCE_DIR  "${CMAKE_CURRENT_SOURCE_DIR}/postgis"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/pgsql-18"
    DEPENDS     postgresql geos proj gdal
    BUILD_IN_SOURCE 1
    PATCH_COMMAND "<SOURCE_DIR>/autogen.sh"
    CONFIGURE_COMMAND
        "<SOURCE_DIR>/configure"
        "CC=gcc"
        "CXX=g++"
        "CFLAGS=${_LAPLACE_CFLAGS}"
        "CXXFLAGS=${_LAPLACE_CFLAGS}"
        "--with-pgconfig=${LAPLACE_DEPS_PREFIX}/pgsql-18/bin/pg_config"
        "--with-geosconfig=${LAPLACE_DEPS_PREFIX}/geos/bin/geos-config"
        "--with-gdalconfig=${LAPLACE_DEPS_PREFIX}/gdal/bin/gdal-config"
        "--with-projdir=${LAPLACE_DEPS_PREFIX}/proj"
    BUILD_COMMAND   make -j ${_NPROC}
    INSTALL_COMMAND make install
)

# ---------------------------------------------------------------------------
# Convenience target: build everything
# ---------------------------------------------------------------------------

add_custom_target(deps ALL
    DEPENDS proj geos gdal tree-sitter postgresql postgis)
