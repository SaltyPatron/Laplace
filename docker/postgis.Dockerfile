# syntax=docker/dockerfile:1.7
# ==============================================================================
# Layer 2: PROJ + GEOS + PostGIS, all from upstream source on the same
# toolchain as Layer 1. Builds against the headers in /opt/pg18 from layer 1.
#
# Result image:  ${IMG_NS}/postgis:${POSTGIS_VERSION}
# ==============================================================================

ARG ONEAPI_HPCKIT=2025.3.1-0-devel-ubuntu22.04
ARG IMG_NS=laplace
ARG POSTGRES_VERSION=18.3
ARG POSTGIS_VERSION=3.6.3
ARG PROJ_VERSION=9.8.1
ARG GEOS_VERSION=3.14.1

# Pull /opt/pg18 from Layer 1 into a named stage we can COPY --from below.
FROM ${IMG_NS}/postgres:${POSTGRES_VERSION} AS pgbase

# ---------- builder ----------
FROM ubuntu:22.04 AS builder
ARG POSTGIS_VERSION
ARG PROJ_VERSION
ARG GEOS_VERSION
ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential pkg-config cmake bison flex \
        autoconf automake libtool m4 \
        libreadline-dev zlib1g-dev libssl-dev libicu-dev \
        libxml2-dev libjson-c-dev libsqlite3-dev sqlite3 libtiff-dev \
        libcurl4-openssl-dev liblz4-dev libzstd-dev uuid-dev \
        ca-certificates perl xsltproc docbook-xsl docbook-xml \
    && rm -rf /var/lib/apt/lists/*

COPY --from=pgbase /opt/pg18 /opt/pg18
ENV PATH=/opt/pg18/bin:$PATH

# Build PROJ/GEOS/PostGIS with stock gcc — same libstdc++ as the runtime image,
# no compiler-bug surface.
ENV CC=gcc CXX=g++ \
    CFLAGS="-O2 -fno-omit-frame-pointer" \
    CXXFLAGS="-O2 -fno-omit-frame-pointer" \
    MAKEFLAGS="-j4"

# ----- PROJ -----
# Pin CMAKE_INSTALL_LIBDIR=lib to defeat GNUInstallDirs multiarch behavior on
# Ubuntu 22.04 which would otherwise install libs to /opt/geo/lib/x86_64-linux-gnu/.
# PostGIS's autoconf-based configure (and pkg-config via /opt/geo/lib/pkgconfig)
# both expect libs at /opt/geo/lib/.
COPY external/proj /src/proj
WORKDIR /src/proj/build
RUN cmake .. \
        -DCMAKE_INSTALL_PREFIX=/opt/geo \
        -DCMAKE_INSTALL_LIBDIR=lib \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_TESTING=OFF \
        -DENABLE_CURL=ON \
        -DENABLE_TIFF=ON && \
    cmake --build . -j4 && \
    cmake --install .

# ----- GEOS -----
COPY external/geos /src/geos
WORKDIR /src/geos/build
RUN cmake .. \
        -DCMAKE_INSTALL_PREFIX=/opt/geo \
        -DCMAKE_INSTALL_LIBDIR=lib \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_TESTING=OFF && \
    cmake --build . -j4 && \
    cmake --install .

# ----- PostGIS -----
# LD_LIBRARY_PATH is required during configure: PostGIS's autoconf does
# AC_TRY_RUN-style link tests that compile + execute a tiny program calling
# initGEOS. GEOS installs without RPATH, so without LD_LIBRARY_PATH the test
# executable fails to find libgeos.so at runtime and configure mistakenly
# concludes libgeos_c is missing.
COPY external/postgis /src/postgis
WORKDIR /src/postgis
RUN ./autogen.sh && \
    PATH=/opt/geo/bin:/opt/pg18/bin:$PATH \
    PKG_CONFIG_PATH=/opt/geo/lib/pkgconfig \
    LD_LIBRARY_PATH=/opt/geo/lib:/opt/pg18/lib \
    ./configure \
        --with-pgconfig=/opt/pg18/bin/pg_config \
        --with-geosconfig=/opt/geo/bin/geos-config \
        --with-projdir=/opt/geo \
        --without-raster \
        --without-protobuf && \
    LD_LIBRARY_PATH=/opt/geo/lib:/opt/pg18/lib make -j4 && \
    make install

# ---------- runtime ----------
FROM ${IMG_NS}/postgres:${POSTGRES_VERSION} AS runtime
USER root

RUN apt-get update && apt-get install -y --no-install-recommends \
        libjson-c5 libsqlite3-0 libtiff5 libcurl4 \
    && rm -rf /var/lib/apt/lists/*

# PROJ + GEOS + PostGIS-installed bits.
COPY --from=builder /opt/geo /opt/geo
COPY --from=builder /opt/pg18/lib/postgresql /opt/pg18/lib/postgresql
COPY --from=builder /opt/pg18/share/postgresql/extension /opt/pg18/share/postgresql/extension
COPY --from=builder /opt/pg18/share/postgresql/contrib /opt/pg18/share/postgresql/contrib

ENV LD_LIBRARY_PATH=/opt/geo/lib:/opt/pg18/lib:/opt/intel/oneapi/redist/lib:/opt/intel/oneapi/redist/lib/intel64:/opt/intel/oneapi/redist/opt/compiler/lib

RUN echo "/opt/geo/lib" > /etc/ld.so.conf.d/geo.conf && ldconfig

USER postgres
