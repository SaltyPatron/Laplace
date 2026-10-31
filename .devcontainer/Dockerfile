# syntax=docker/dockerfile:1.7
FROM ubuntu:22.04

LABEL org.opencontainers.image.title="Laplace Multi-Repository Devcontainer" \
      org.opencontainers.image.description="Prebuilt toolchain for Laplace (Intel oneAPI icx/oneMKL, PostgreSQL 18.6 + PostGIS, ICU 78, CORE-MATH, BLAKE3, Tree-sitter, .NET 9, Node.js 22)" \
      org.opencontainers.image.source="https://github.com/SaltyPatron/Laplace"

ENV DEBIAN_FRONTEND=noninteractive \
    LAPLACE_SRC=/repos/src \
    LAPLACE_BUILD=/repos/build \
    LAPLACE_WORK=/repos/work \
    LAPLACE_DEPS=/repos/deps \
    LAPLACE_DEPSRC=/repos/src \
    LAPLACE_DATA=/vault/Data \
    LAPLACE_MODELS=/vault/Models \
    LAPLACE_PREFIX=/usr/local \
    LAPLACE_PG_DIR=/usr/local/pgsql \
    LAPLACE_PGDATA=/data/pgdata \
    LAPLACE_PGWAL=/data/pgwal \
    LAPLACE_PGTEMP=/data/pgtemp \
    LAPLACE_PGUSER=postgres \
    LAPLACE_ROLE=laplace \
    LAPLACE_GROUP=laplace-runner \
    LAPLACE_PG_GROUP=postgres-extensions \
    LAPLACE_ICU_DIR=/repos/deps/icu78 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# 1. Base system packages, build dependencies, and developer utilities
RUN apt-get update -q && apt-get install -y --no-install-recommends \
    build-essential cmake ninja-build meson pkg-config flex bison \
    liburing-dev libssl-dev liblz4-dev libzstd-dev libreadline-dev \
    zlib1g-dev libxml2-dev libsqlite3-dev libtiff-dev libcurl4-openssl-dev \
    libprotobuf-c-dev protobuf-c-compiler libjson-c-dev libeigen3-dev \
    xfsprogs lvm2 openssl sqlite3 autoconf automake libtool \
    libgmp-dev libmpfr-dev curl ca-certificates git sudo jq python3 \
    python3-pip python3-venv locales gnupg wget unzip xz-utils \
    && locale-gen en_US.UTF-8 C.UTF-8 \
    && rm -rf /var/lib/apt/lists/*

# 2. Modern CMake (Kitware latest release)
RUN v=$(curl -fsSL https://api.github.com/repos/Kitware/CMake/releases/latest | sed -n 's/.*"tag_name": *"v\([^"]*\)".*/\1/p') \
    && curl -fsSL "https://github.com/Kitware/CMake/releases/download/v${v}/cmake-${v}-linux-x86_64.tar.gz" \
       | tar xz -C /usr/local --strip-components=1 --exclude='*/doc' --exclude='*/man'

# 3. Intel oneAPI Base & HPC Compiler + oneMKL (icx, icpx, MKL)
RUN curl -fsSL https://apt.repos.intel.com/intel-gpg-keys/GPG-PUB-KEY-INTEL-SW-PRODUCTS.PUB \
    | gpg --dearmor -o /usr/share/keyrings/oneapi-archive-keyring.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/oneapi-archive-keyring.gpg] https://apt.repos.intel.com/oneapi all main" \
       > /etc/apt/sources.list.d/oneAPI.list \
    && apt-get update -q \
    && apt-get install -y --no-install-recommends \
       intel-oneapi-compiler-dpcpp-cpp \
       intel-oneapi-mkl-devel \
    && rm -rf /var/lib/apt/lists/*

# 4. .NET 10 SDK, Node.js 24 LTS, and GitHub CLI
RUN curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -o /tmp/ms-prod.deb \
    && dpkg -i /tmp/ms-prod.deb && rm -f /tmp/ms-prod.deb \
    && curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
       | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg \
    && chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
       > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update -q \
    && apt-get install -y --no-install-recommends dotnet-sdk-10.0 nodejs gh \
    && rm -rf /var/lib/apt/lists/*

# 5. Users, groups, and Laplace directory layout
RUN groupadd -r "${LAPLACE_GROUP}" \
    && groupadd -r "${LAPLACE_PG_GROUP}" \
    && useradd -r -m -d "/var/lib/${LAPLACE_PGUSER}" -s /bin/bash "${LAPLACE_PGUSER}" \
    && useradd -m -d /home/vscode -s /bin/bash -G sudo,"${LAPLACE_GROUP}","${LAPLACE_PG_GROUP}" vscode \
    && echo "vscode ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/vscode \
    && chmod 0440 /etc/sudoers.d/vscode \
    && install -d -m 2775 -g "${LAPLACE_GROUP}" \
       "${LAPLACE_SRC}" "${LAPLACE_BUILD}" "${LAPLACE_WORK}" "${LAPLACE_DEPS}" \
       "${LAPLACE_DATA}" "${LAPLACE_MODELS}" /run/lock/laplace /opt/laplace \
    && install -d -o "${LAPLACE_PGUSER}" -g "${LAPLACE_PGUSER}" -m 0700 \
       "${LAPLACE_PGDATA}" "${LAPLACE_PGWAL}" "${LAPLACE_PGTEMP}"

# 6. ICU 78.3 (Unicode 17) + ld.so.conf registration for ICU and oneAPI runtime
RUN t=$(mktemp -d) \
    && curl -fsSL https://github.com/unicode-org/icu/releases/download/release-78.3/icu4c-78.3-Ubuntu22.04-x64.tgz \
       | tar xz -C "$t" \
    && install -d "${LAPLACE_ICU_DIR}" \
    && cp -a "$t/icu/usr/local/." "${LAPLACE_ICU_DIR}/" \
    && rm -rf "$t" \
    && printf '%s\n' "${LAPLACE_ICU_DIR}/lib" > /etc/ld.so.conf.d/laplace-icu.conf \
    && rt=$(ls -d /opt/intel/oneapi/compiler/[0-9]*/lib 2>/dev/null | sort -V | tail -1) \
    && [ -n "$rt" ] && printf '%s\n' "$rt" > /etc/ld.so.conf.d/laplace-oneapi.conf \
    && mkl=$(ls -d /opt/intel/oneapi/mkl/[0-9]*/lib 2>/dev/null | sort -V | tail -1) \
    && [ -n "$mkl" ] && printf '%s\n' "$mkl" >> /etc/ld.so.conf.d/laplace-oneapi.conf \
    && ldconfig

# 7. Native source dependencies (BLAKE3, CORE-MATH, Tree-sitter, Fathom, Spectra, GEOS, PROJ, GDAL, PostgreSQL 18, PostGIS)
RUN set -eux; \
    . /opt/intel/oneapi/setvars.sh >/dev/null 2>&1 || true; \
    cores=$(nproc); \
    git clone -q --depth 1 https://github.com/BLAKE3-team/BLAKE3.git "${LAPLACE_DEPSRC}/blake3"; \
    git clone -q --depth 1 https://gitlab.inria.fr/core-math/core-math.git "${LAPLACE_DEPSRC}/core-math"; \
    git clone -q --depth 1 https://github.com/tree-sitter/tree-sitter.git "${LAPLACE_DEPSRC}/tree-sitter"; \
    git clone -q --depth 1 https://github.com/jdart1/Fathom.git "${LAPLACE_DEPSRC}/Fathom"; \
    git clone -q --depth 1 https://github.com/yixuan/spectra.git "${LAPLACE_DEPSRC}/spectra"; \
    install -d "${LAPLACE_PREFIX}/include/Spectra"; \
    cp -a "${LAPLACE_DEPSRC}/spectra/include/Spectra/." "${LAPLACE_PREFIX}/include/Spectra/"; \
    for repo_pair in \
        "geos https://github.com/libgeos/geos.git main" \
        "proj https://github.com/OSGeo/PROJ.git master" \
        "gdal https://github.com/OSGeo/gdal.git master"; do \
        set -- $repo_pair; name=$1; url=$2; branch=$3; \
        git clone -q --depth 1 -b "$branch" "$url" "${LAPLACE_DEPSRC}/${name}"; \
        cmake -S "${LAPLACE_DEPSRC}/${name}" -B "${LAPLACE_WORK}/${name}" -G Ninja \
            -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="${LAPLACE_PREFIX}" -DBUILD_TESTING=OFF; \
        cmake --build "${LAPLACE_WORK}/${name}" -j "$cores"; \
        cmake --install "${LAPLACE_WORK}/${name}"; \
        ldconfig; \
        rm -rf "${LAPLACE_WORK}/${name}"; \
    done; \
    git clone -q --depth 1 -b REL_18_STABLE https://git.postgresql.org/git/postgresql.git "${LAPLACE_DEPSRC}/postgresql"; \
    mkdir -p "${LAPLACE_WORK}/postgresql"; \
    cd "${LAPLACE_WORK}/postgresql"; \
    CC=icx CXX=icpx "${LAPLACE_DEPSRC}/postgresql/configure" \
        --prefix="${LAPLACE_PG_DIR}" --with-icu --with-ssl=openssl --with-lz4 --with-zstd --with-liburing \
        ICU_CFLAGS="-I${LAPLACE_ICU_DIR}/include" ICU_LIBS="-L${LAPLACE_ICU_DIR}/lib -licui18n -licuuc -licudata"; \
    make -j "$cores"; \
    make install; \
    for m in pg_stat_statements pg_buffercache auto_explain pg_walinspect file_fdw; do \
        make -C "${LAPLACE_WORK}/postgresql/contrib/${m}" install; \
    done; \
    ln -sfn "${LAPLACE_PG_DIR}/bin/psql" "${LAPLACE_PREFIX}/bin/psql"; \
    ln -sfn "${LAPLACE_PG_DIR}/bin/pg_config" "${LAPLACE_PREFIX}/bin/pg_config"; \
    git clone -q --depth 1 https://git.osgeo.org/gitea/postgis/postgis.git "${LAPLACE_DEPSRC}/postgis"; \
    cd "${LAPLACE_DEPSRC}/postgis" && ./autogen.sh; \
    mkdir -p "${LAPLACE_WORK}/postgis" && cd "${LAPLACE_WORK}/postgis"; \
    "${LAPLACE_DEPSRC}/postgis/configure" \
        --with-pgconfig="${LAPLACE_PG_DIR}/bin/pg_config" \
        --with-geosconfig="${LAPLACE_PREFIX}/bin/geos-config" \
        --with-projdir="${LAPLACE_PREFIX}" \
        --with-gdalconfig="${LAPLACE_PREFIX}/bin/gdal-config"; \
    make -j "$cores"; \
    make install; \
    ldconfig; \
    install -d -m 2775 -g "${LAPLACE_PG_GROUP}" "${LAPLACE_PG_DIR}/lib" "${LAPLACE_PG_DIR}/share/extension"; \
    chgrp -R "${LAPLACE_GROUP}" "${LAPLACE_SRC}" "${LAPLACE_BUILD}" "${LAPLACE_WORK}" "${LAPLACE_DEPS}"; \
    chmod -R g+rwX "${LAPLACE_SRC}" "${LAPLACE_BUILD}" "${LAPLACE_WORK}" "${LAPLACE_DEPS}"

# 8. Machine environment declaration (/etc/laplace/machine.env) for container/Codespace runtime
RUN install -d -m 0755 /etc/laplace \
    && cat > /etc/laplace/machine.env <<'EOF'
LAPLACE_SRC=/repos/src
LAPLACE_BUILD=/repos/build
LAPLACE_WORK=/repos/work
LAPLACE_DEPS=/repos/deps
LAPLACE_DEPSRC=/repos/src
LAPLACE_DATA=/vault/Data
LAPLACE_MODELS=/vault/Models
LAPLACE_PREFIX=/usr/local
LAPLACE_PG_DIR=/usr/local/pgsql
LAPLACE_PGDATA=/data/pgdata
LAPLACE_PGWAL=/data/pgwal
LAPLACE_PGTEMP=/data/pgtemp
LAPLACE_PGUSER=postgres
LAPLACE_ROLE=laplace
LAPLACE_PGPORT=5432
LAPLACE_CONNINFO="host=/tmp port=5432 user=laplace dbname=laplace"
LAPLACE_DBNAME=laplace-mono
LAPLACE_APP_DBNAME=laplace-app
EOF
RUN chmod 0644 /etc/laplace/machine.env

ENV PATH="${LAPLACE_PG_DIR}/bin:/usr/local/bin:${PATH}"
USER vscode
WORKDIR /workspaces
