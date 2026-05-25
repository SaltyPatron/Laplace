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

# Single source of truth for external dependency trees. Defaults to the
# in-repo external/ (this directory) for local dev where workspace
# submodules are initialized. CI overrides with -DLAPLACE_EXTERNAL=/opt/laplace/external
# so the build reads from the persistent host-side checkout directly —
# no workspace .git/modules, no --reference alternates, no per-job re-init.
set(LAPLACE_EXTERNAL "${CMAKE_CURRENT_SOURCE_DIR}" CACHE PATH
    "Path to the external/ dependency source tree")

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
    SOURCE_DIR  "${LAPLACE_EXTERNAL}/proj"
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
    SOURCE_DIR  "${LAPLACE_EXTERNAL}/geos"
    BINARY_DIR  "${CMAKE_BINARY_DIR}/geos-build"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/geos"
    CMAKE_ARGS
        ${_COMMON_CMAKE_ARGS}
        "-DCMAKE_INSTALL_PREFIX=<INSTALL_DIR>"
        # CMAKE_INSTALL_RPATH points the installed libgeos_c.so at our
        # libgeos.so so dependents (postgis-3.so, our laplace_geom.so,
        # any process loading libgeos_c) find the matching libgeos.so.X.Y.Z
        # via RUNPATH instead of falling through to ld.so.cache (where
        # SYSTEM libgeos 3.10.2 lives under the SAME SONAME — ABI mismatch).
        # Without this RUNPATH, postgis-3.so + laplace_geom.so end up
        # using system 3.10.2 (missing the GEOS 3.11+ symbols PostGIS
        # 3.6.3 requires, e.g. GEOSConcaveHullOfPolygons).
        "-DCMAKE_INSTALL_RPATH=${LAPLACE_DEPS_PREFIX}/geos/lib"
    BUILD_COMMAND
        "${CMAKE_COMMAND}" --build <BINARY_DIR> -j ${_NPROC}
)

# ---------------------------------------------------------------------------
# GDAL — geospatial data abstraction (CMake-based, depends on PROJ)
# ---------------------------------------------------------------------------

ExternalProject_Add(gdal
    SOURCE_DIR  "${LAPLACE_EXTERNAL}/gdal"
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
    SOURCE_DIR  "${LAPLACE_EXTERNAL}/tree-sitter"
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
    SOURCE_DIR  "${LAPLACE_EXTERNAL}/postgresql"
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
    # submake-generated-headers must run before -j parallel build, otherwise
    # src/common/hashfn.c races errcodes.h generation. The target has a
    # MAKELEVEL=0 guard in src/Makefile.global.in:
    #   submake-generated-headers:
    #       ifeq ($(MAKELEVEL),0)
    #           $(MAKE) -C $(top_builddir)/src/backend generated-headers
    #       endif
    # When ExternalProject invokes our BUILD_COMMAND, this make is a sub-make
    # of CMake's outer make → MAKELEVEL > 0 → the guard makes the target a
    # silent no-op → errcodes.h never gets generated → the next -j make fails
    # immediately on src/common/hashfn.c. Force MAKELEVEL=0 to bypass.
    BUILD_COMMAND   env -u PGXS MAKELEVEL=0 make submake-generated-headers
            COMMAND env -u PGXS make -j ${_NPROC}
    # `make install` alone omits contrib/ (fuzzystrmatch, etc.). PostGIS
    # extensions such as postgis_tiger_geocoder require fuzzystrmatch; a
    # substrate-owned PG must ship the usual contrib extension set.
    INSTALL_COMMAND env -u PGXS MAKELEVEL=0 make install
            COMMAND env -u PGXS MAKELEVEL=0 make -C contrib install
)

# ---------------------------------------------------------------------------
# PostGIS 3.6.3 — autoconf, in-source build (does not VPATH cleanly).
# Depends on PG + GEOS + PROJ + GDAL all being installed first.
# autogen.sh runs as PATCH_COMMAND to generate ./configure from configure.ac.
# ---------------------------------------------------------------------------

# PostGIS's configure runs AC_CHECK_LIB-style link tests for initGEOS in
# -lgeos_c (and similar for proj/gdal). Those tests use the host linker
# search path — which doesn't include /opt/laplace/{geos,proj,gdal}/lib
# even though --with-{geos,proj,gdal} are passed. Pass an explicit LDFLAGS
# with -L plus -Wl,-rpath so (a) the link tests succeed and (b) the
# resulting postgis.so finds the right libs at load time.
#
# Also strip PGXS for the same reason as the postgresql target above —
# PostGIS reads it during configure to decide which PG it's targeting,
# and a stale shell export would point at the system PG instead of our
# /opt/laplace/pgsql-18 one.
set(_POSTGIS_LDFLAGS
    "-L${LAPLACE_DEPS_PREFIX}/geos/lib -Wl,-rpath,${LAPLACE_DEPS_PREFIX}/geos/lib \
-L${LAPLACE_DEPS_PREFIX}/proj/lib -Wl,-rpath,${LAPLACE_DEPS_PREFIX}/proj/lib \
-L${LAPLACE_DEPS_PREFIX}/gdal/lib -Wl,-rpath,${LAPLACE_DEPS_PREFIX}/gdal/lib")

ExternalProject_Add(postgis
    SOURCE_DIR  "${LAPLACE_EXTERNAL}/postgis"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/pgsql-18"
    DEPENDS     postgresql geos proj gdal
    BUILD_IN_SOURCE 1
    PATCH_COMMAND "<SOURCE_DIR>/autogen.sh"
    CONFIGURE_COMMAND
        env -u PGXS
        "<SOURCE_DIR>/configure"
        "CC=gcc"
        "CXX=g++"
        "CFLAGS=${_LAPLACE_CFLAGS}"
        "CXXFLAGS=${_LAPLACE_CFLAGS}"
        "LDFLAGS=${_POSTGIS_LDFLAGS}"
        # --with-pgconfig routes extension .so + .sql + .control into PG's
        # tree (/opt/laplace/pgsql-18/{lib,share/extension}), but PostGIS's
        # topology Makefile install-importer hardcodes `$(bindir)` for
        # pgtopo_import, which falls back to autoconf's /usr/local/bin
        # default — root-owned, install fails. --prefix + --bindir +
        # --datarootdir pin every install dest under /opt/laplace/pgsql-18.
        "--prefix=${LAPLACE_DEPS_PREFIX}/pgsql-18"
        "--bindir=${LAPLACE_DEPS_PREFIX}/pgsql-18/bin"
        "--datarootdir=${LAPLACE_DEPS_PREFIX}/pgsql-18/share"
        "--with-pgconfig=${LAPLACE_DEPS_PREFIX}/pgsql-18/bin/pg_config"
        "--with-geosconfig=${LAPLACE_DEPS_PREFIX}/geos/bin/geos-config"
        "--with-gdalconfig=${LAPLACE_DEPS_PREFIX}/gdal/bin/gdal-config"
        "--with-projdir=${LAPLACE_DEPS_PREFIX}/proj"
    BUILD_COMMAND   env -u PGXS make -j ${_NPROC}
    INSTALL_COMMAND env -u PGXS make install
    # PostGIS upstream's make install does NOT install liblwgeom standalone —
    # only postgis-3.so (with lwgeom statically baked in). The laplace_geom
    # extension needs the lwgeom API at compile time to deserialize PostGIS
    # `geometry` Datums into POINT4D coords for the ST_*_4d engine kernels.
    # postgis-3.so itself hides those symbols (-fvisibility=hidden — only 9
    # lwgeom_* symbols exported), so we statically link the lwgeom artifacts
    # PostGIS builds anyway. Install:
    #   include/liblwgeom.h        — main API header
    #   include/lwinline.h         — inline helpers liblwgeom.h #includes
    #   postgis_config.h           — autotools-generated config, included by
    #                                liblwgeom.h via `../postgis_config.h`
    #                                relative path; living at the include
    #                                dir's parent makes the relative path
    #                                resolve correctly when the extension
    #                                compiles with -I<prefix>/include.
    #   lib/liblwgeom.a            — static library
    COMMAND ${CMAKE_COMMAND} -E copy
            "<SOURCE_DIR>/liblwgeom/liblwgeom.h"
            "${LAPLACE_DEPS_PREFIX}/pgsql-18/include/liblwgeom.h"
    COMMAND ${CMAKE_COMMAND} -E copy
            "<SOURCE_DIR>/liblwgeom/lwinline.h"
            "${LAPLACE_DEPS_PREFIX}/pgsql-18/include/lwinline.h"
    COMMAND ${CMAKE_COMMAND} -E copy
            "<SOURCE_DIR>/postgis_config.h"
            "${LAPLACE_DEPS_PREFIX}/pgsql-18/postgis_config.h"
    COMMAND ${CMAKE_COMMAND} -E copy
            "<SOURCE_DIR>/postgis_revision.h"
            "${LAPLACE_DEPS_PREFIX}/pgsql-18/postgis_revision.h"
    COMMAND ${CMAKE_COMMAND} -E copy
            "<SOURCE_DIR>/liblwgeom/.libs/liblwgeom.a"
            "${LAPLACE_DEPS_PREFIX}/pgsql-18/lib/liblwgeom.a"
)

# ---------------------------------------------------------------------------
# Convenience target: build everything
# ---------------------------------------------------------------------------

add_custom_target(deps ALL
    DEPENDS proj geos gdal tree-sitter postgresql postgis)
