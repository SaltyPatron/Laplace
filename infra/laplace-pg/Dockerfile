# laplace-pg --- Laplace substrate Postgres + PostGIS + laplace_pg extension.
#
# Base: postgis/postgis:18-3.6 --- neutral upstream from the postgis project,
# debian-bookworm-based, ~150 MB, derived from the official postgres:18-bookworm
# image. Has PG 18 + PostGIS-3 + the standard postgres docker-entrypoint.sh
# (handles POSTGRES_USER/PASSWORD/DB env vars and /docker-entrypoint-initdb.d/).
#
# Multi-stage:
#   builder: same base + apt build-essential + cmake + git + pkg-config +
#            postgresql-server-dev-18. Compiles laplace_pg.so against the
#            actual PG 18 ABI in the image. BLAKE3 + Eigen + Spectra come in
#            via FetchContent during cmake configure.
#   runtime: same base + .so + .control + .sql copied in + initdb scripts.
#
# Substrate identity (extension = laplace_pg, schema = laplace, type family =
# GEOMETRY4D) is independent of postgis per CLAUDE.md invariant 5; postgis is
# enabled additively for naturally-low-dim modalities.
#
# MKL-dependent native services (B15 KnnExact, B20 FFT) are gated behind
# MKL_FOUND in CMakeLists; this base lacks Intel oneAPI so those services
# are absent from the .so. They land when MKL is layered in separately.

ARG PG_BASE=postgis/postgis:18-3.6

# -----------------------------------------------------------------------------
FROM ${PG_BASE} AS builder

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        build-essential \
        cmake \
        git \
        ca-certificates \
        pkg-config \
        postgresql-server-dev-18 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY CMakeLists.txt /src/CMakeLists.txt
COPY ext/laplace_pg/ /src/ext/laplace_pg/

RUN cmake -S /src -B /build \
        -DCMAKE_BUILD_TYPE=Release \
 && cmake --build /build --target laplace_pg --parallel

# Manual install --- targeted, deterministic. BLAKE3 is statically linked into
# laplace_pg.so so we don't need its install rules (which trip on a missing
# libblake3.pc that BLAKE3 only generates as build-root). Paths come from
# pg_config so they match the apt-installed PG 18 layout.
RUN PG_PKGLIBDIR=$(pg_config --pkglibdir) \
 && PG_SHAREDIR=$(pg_config --sharedir) \
 && install -m 0755 /build/ext/laplace_pg/laplace_pg.so \
        "${PG_PKGLIBDIR}/laplace_pg.so" \
 && install -m 0644 /src/ext/laplace_pg/sql/laplace_pg.control \
        "${PG_SHAREDIR}/extension/laplace_pg.control" \
 && for f in /src/ext/laplace_pg/sql/laplace_pg--*.sql; do \
        install -m 0644 "$f" "${PG_SHAREDIR}/extension/$(basename "$f")"; \
    done \
 && echo "=== installed extension files ===" \
 && ls -la "${PG_PKGLIBDIR}/laplace_pg"* \
 && ls -la "${PG_SHAREDIR}/extension/laplace_pg"*

# -----------------------------------------------------------------------------
FROM ${PG_BASE} AS runtime

COPY --from=builder /usr/lib/postgresql/18/lib/laplace_pg.so \
                    /usr/lib/postgresql/18/lib/laplace_pg.so
COPY --from=builder /usr/share/postgresql/18/extension/laplace_pg.control \
                    /usr/share/postgresql/18/extension/laplace_pg.control
COPY --from=builder /usr/share/postgresql/18/extension/laplace_pg--0.1.0.sql \
                    /usr/share/postgresql/18/extension/laplace_pg--0.1.0.sql

COPY infra/laplace-pg/initdb.d/ /docker-entrypoint-initdb.d/

ENV POSTGRES_DB=laplace
