# syntax=docker/dockerfile:1.7
# ==============================================================================
# Layer 3: laplace_native shared library + laplace_pg PostgreSQL extension.
#
# Builds against pg headers from Layer 1 (inherited via Layer 2), Eigen +
# Spectra + BLAKE3 fetched at configure time, Intel oneAPI MKL + TBB linked
# from the hpckit base image, CGAL for Voronoi4DService, and the generated
# codepoint table + registries from ext/laplace_pg/generated/ (compiled into
# the laplace_generated static lib that's linked into both targets).
#
# Result image:  ${IMG_NS}/pgext:dev
# ==============================================================================

ARG ONEAPI_HPCKIT=2025.3.1-0-devel-ubuntu22.04
ARG IMG_NS=laplace
ARG POSTGIS_VERSION=3.6.3

# Pull layer 2 contents (pg + geo + postgis) into a named stage.
FROM ${IMG_NS}/postgis:${POSTGIS_VERSION} AS pgisbase

# ---------- builder ----------
FROM intel/oneapi-hpckit:${ONEAPI_HPCKIT} AS builder
ENV DEBIAN_FRONTEND=noninteractive TZ=Etc/UTC

# Complete dev toolset for laplace_native + laplace_pg.
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential pkg-config cmake git ca-certificates \
        bison flex autoconf automake libtool m4 perl \
        libreadline-dev zlib1g-dev libssl-dev libicu-dev libxml2-dev \
        liblz4-dev libzstd-dev uuid-dev \
        libcgal-dev libtbb-dev \
    && rm -rf /var/lib/apt/lists/*

SHELL ["/bin/bash", "-lc"]
ENV ONEAPI_ROOT=/opt/intel/oneapi

# Bring postgres + postgis + geo from prior layer.
COPY --from=pgisbase /opt/pg18 /opt/pg18
COPY --from=pgisbase /opt/geo /opt/geo
ENV PATH=/opt/pg18/bin:$PATH

# Substrate sources. Top-level CMakeLists.txt drives the build via
# add_subdirectory(ext/laplace_pg); the generated codepoint tables (.c + .h)
# inside ext/laplace_pg/generated/ compile into laplace_generated.lib.
WORKDIR /src
COPY CMakeLists.txt /src/CMakeLists.txt
COPY ext/laplace_pg /src/ext/laplace_pg

# CMake configure + build. icx for C numerical kernels (substrate's MKL/TBB
# glue), g++ for C++ (Eigen + Spectra are header-only and icpx 2025.3.x
# segfaults compiling them — documented gotcha). Both compilers emit C-ABI
# compatible code under the same libstdc++. RelWithDebInfo + frame pointers
# so the in-extension crash handler can resolve symbols via addr2line on .so.
RUN source ${ONEAPI_ROOT}/setvars.sh --force && \
    CC=icx CXX=g++ \
    cmake -S /src -B /build \
        -DCMAKE_BUILD_TYPE=RelWithDebInfo \
        -DCMAKE_C_FLAGS_RELWITHDEBINFO="-O2 -g3 -fno-omit-frame-pointer" \
        -DCMAKE_CXX_FLAGS_RELWITHDEBINFO="-O2 -g3 -fno-omit-frame-pointer" \
        -DPostgreSQL_ROOT=/opt/pg18 \
        -DCMAKE_PREFIX_PATH="/opt/geo" && \
    cmake --build /build --target laplace_pg --parallel "$(nproc)" && \
    cmake --build /build --target laplace_native --parallel "$(nproc)"

# Manual install — substrate's CMakeLists has install(TARGETS) but the prefix
# layout doesn't match pg_config's $pkglibdir. Install by hand using canonical
# pg_config paths so PG finds the extension at standard locations.
#
# laplace_native is the MANAGED P/Invoke surface — it lives outside the PG
# container in the .NET CLI process. Building it here verifies the same source
# compiles cleanly under the substrate's canonical icx/g++ toolchain, but the
# runtime image only deploys the laplace_pg extension.
RUN PG_PKGLIBDIR=$(/opt/pg18/bin/pg_config --pkglibdir) && \
    PG_SHAREDIR=$(/opt/pg18/bin/pg_config --sharedir) && \
    install -d "$PG_PKGLIBDIR" "$PG_SHAREDIR/extension" && \
    install -m 0755 /build/ext/laplace_pg/laplace_pg.so "$PG_PKGLIBDIR/laplace_pg.so" && \
    install -m 0644 /src/ext/laplace_pg/sql/laplace_pg.control "$PG_SHAREDIR/extension/laplace_pg.control" && \
    for f in /src/ext/laplace_pg/sql/laplace_pg--*.sql; do \
        install -m 0644 "$f" "$PG_SHAREDIR/extension/$(basename "$f")"; \
    done && \
    echo "=== installed extension files ===" && \
    ls -la "$PG_PKGLIBDIR/laplace_pg"* && \
    ls -la "$PG_SHAREDIR/extension/laplace_pg"*

# ---------- runtime ----------
FROM ${IMG_NS}/postgis:${POSTGIS_VERSION} AS runtime
USER root

# gdb + binutils for backtrace from core dumps. CGAL + TBB runtime libs for
# Voronoi4DService and parallel kernels reachable from inside the extension.
RUN apt-get update && apt-get install -y --no-install-recommends \
        gdb binutils libcgal13 libtbb12 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /opt/pg18/lib/postgresql/laplace_pg.so /opt/pg18/lib/postgresql/laplace_pg.so
COPY --from=builder /opt/pg18/share/postgresql/extension/laplace_pg.control /opt/pg18/share/postgresql/extension/laplace_pg.control
COPY --from=builder /opt/pg18/share/postgresql/extension/laplace_pg--*.sql /opt/pg18/share/postgresql/extension/

RUN ldconfig

USER postgres
