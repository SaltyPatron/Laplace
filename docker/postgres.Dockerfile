# syntax=docker/dockerfile:1.7
# ==============================================================================
# Layer 1: PostgreSQL from upstream source, built on Intel oneAPI base so
# downstream layers (PostGIS, laplace_pg, laplace_native) inherit one
# consistent libstdc++ + glibc + libc++ across the entire stack.
#
# Result image:  ${IMG_NS}/postgres:${POSTGRES_VERSION}
# ==============================================================================

ARG ONEAPI_HPCKIT=2025.3.1-0-devel-ubuntu22.04
ARG ONEAPI_RUNTIME=2025.3.1-0-devel-ubuntu22.04
ARG POSTGRES_VERSION=18.3

# ---------- builder ----------
FROM intel/oneapi-hpckit:${ONEAPI_HPCKIT} AS builder
ARG POSTGRES_VERSION
ENV DEBIAN_FRONTEND=noninteractive TZ=Etc/UTC

RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential pkg-config bison flex \
        libreadline-dev zlib1g-dev libssl-dev libicu-dev libxml2-dev \
        liblz4-dev libzstd-dev uuid-dev libsystemd-dev \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

SHELL ["/bin/bash", "-lc"]

# external/postgres is a git submodule pinned in versions.env (POSTGRES_VERSION).
# Compile from upstream source so we control build flags and link against the
# same libstdc++ as every downstream layer.
COPY external/postgres /src/postgres
WORKDIR /src/postgres

# PostgreSQL is pure-C glue. Build with stock gcc (icx is not certified for
# postgres and crashes on pl_exec.c at -O3 -xHost). icx/icpx is reserved for
# laplace_native numerical kernels in Layer 3 where it actually wins; both
# compilers emit C-ABI compatible code under the system libstdc++.
#
# Production-mode flags: -O2 with debug info preserved (-g) and frame pointers
# kept so the in-extension signal handler can still walk stacks. JIT off (LLVM
# is heavy and the substrate doesn't use it).
RUN CC=gcc CXX=g++ \
    CFLAGS="-O2 -g -fno-omit-frame-pointer" \
    CXXFLAGS="-O2 -g -fno-omit-frame-pointer" \
    ./configure \
        --prefix=/opt/pg18 \
        --with-icu \
        --with-openssl \
        --with-libxml \
        --with-uuid=e2fs \
        --with-lz4 \
        --with-zstd \
        --with-system-tzdata=/usr/share/zoneinfo \
        --without-llvm

RUN make -j"$(nproc)" world-bin && \
    make install-world-bin

# ---------- runtime ----------
FROM intel/oneapi-runtime:${ONEAPI_RUNTIME} AS runtime
ARG POSTGRES_VERSION
ENV DEBIAN_FRONTEND=noninteractive TZ=Etc/UTC

RUN apt-get update && apt-get install -y --no-install-recommends \
        libreadline8 zlib1g libssl3 libicu70 libxml2 liblz4-1 libzstd1 \
        libuuid1 tzdata locales ca-certificates \
        gosu \
    && rm -rf /var/lib/apt/lists/* \
    && localedef -i en_US -c -f UTF-8 -A /usr/share/locale/locale.alias en_US.UTF-8

ENV LANG=en_US.utf8 \
    PG_MAJOR=18 \
    PGDATA=/var/lib/postgresql/data \
    PATH=/opt/pg18/bin:$PATH \
    LD_LIBRARY_PATH=/opt/pg18/lib:/opt/intel/oneapi/redist/lib:/opt/intel/oneapi/redist/lib/intel64:/opt/intel/oneapi/redist/opt/compiler/lib

COPY --from=builder /opt/pg18 /opt/pg18

RUN groupadd -r postgres --gid=999 && \
    useradd -r -g postgres --uid=999 --home-dir=/var/lib/postgresql --shell=/bin/bash postgres && \
    install -d -o postgres -g postgres -m 0700 /var/lib/postgresql /var/lib/postgresql/data && \
    install -d -m 1777 /var/run/postgresql

RUN echo "/opt/pg18/lib" > /etc/ld.so.conf.d/pg18.conf && \
    echo "/opt/intel/oneapi/redist/lib" > /etc/ld.so.conf.d/oneapi.conf && \
    echo "/opt/intel/oneapi/redist/lib/intel64" >> /etc/ld.so.conf.d/oneapi.conf && \
    echo "/opt/intel/oneapi/redist/opt/compiler/lib" >> /etc/ld.so.conf.d/oneapi.conf && \
    ldconfig

EXPOSE 5432
VOLUME /var/lib/postgresql/data

USER postgres
CMD ["postgres"]
