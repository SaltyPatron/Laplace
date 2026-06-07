cmake_minimum_required(VERSION 3.22)
project(laplace-deps LANGUAGES NONE)

include(ExternalProject)
include(ProcessorCount)

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

if(NOT EXISTS "${LAPLACE_DEPS_PREFIX}")
    message(FATAL_ERROR
        "${LAPLACE_DEPS_PREFIX} does not exist. Run `sudo just bootstrap` "
        "to set it up (Layer-0; one-time per machine, per ADR 0019).")
endif()

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

set(_COMMON_CMAKE_ARGS
    "-DCMAKE_TOOLCHAIN_FILE=${LAPLACE_DEPS_TOOLCHAIN}"
    "-DCMAKE_BUILD_TYPE=Release"
    "-DBUILD_TESTING=OFF"
    "-DBUILD_SHARED_LIBS=ON"
)

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

ExternalProject_Add(geos
    SOURCE_DIR  "${LAPLACE_EXTERNAL}/geos"
    BINARY_DIR  "${CMAKE_BINARY_DIR}/geos-build"
    INSTALL_DIR "${LAPLACE_DEPS_PREFIX}/geos"
    CMAKE_ARGS
        ${_COMMON_CMAKE_ARGS}
        "-DCMAKE_INSTALL_PREFIX=<INSTALL_DIR>"
        "-DCMAKE_INSTALL_RPATH=${LAPLACE_DEPS_PREFIX}/geos/lib"
    BUILD_COMMAND
        "${CMAKE_COMMAND}" --build <BINARY_DIR> -j ${_NPROC}
)

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
    BUILD_COMMAND   env -u PGXS MAKELEVEL=0 make submake-generated-headers
            COMMAND env -u PGXS make -j ${_NPROC}
    INSTALL_COMMAND env -u PGXS MAKELEVEL=0 make install
            COMMAND env -u PGXS MAKELEVEL=0 make -C contrib install
)

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
        "--prefix=${LAPLACE_DEPS_PREFIX}/pgsql-18"
        "--bindir=${LAPLACE_DEPS_PREFIX}/pgsql-18/bin"
        "--datarootdir=${LAPLACE_DEPS_PREFIX}/pgsql-18/share"
        "--with-pgconfig=${LAPLACE_DEPS_PREFIX}/pgsql-18/bin/pg_config"
        "--with-geosconfig=${LAPLACE_DEPS_PREFIX}/geos/bin/geos-config"
        "--with-gdalconfig=${LAPLACE_DEPS_PREFIX}/gdal/bin/gdal-config"
        "--with-projdir=${LAPLACE_DEPS_PREFIX}/proj"
    BUILD_COMMAND   env -u PGXS make -j ${_NPROC}
    INSTALL_COMMAND env -u PGXS make install
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

add_custom_target(deps ALL
    DEPENDS proj geos gdal tree-sitter postgresql postgis)
