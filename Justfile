# Justfile — canonical command runner for Laplace
# Run `just` with no arguments to list all commands.
# See OPERATIONS.md for full operational reference.

set shell := ["bash", "-uc"]

# Default: show available commands
default:
    @just --list

# === Environment ===

check-prereqs:
    @scripts/check-prereqs.sh

# === Layer 0 — root bootstrap (system account, runner, PG roles, peer auth) ===
# Per ADR 0018 (three-layer architecture) + ADR 0019 (laplace-runner).
# These wrap scripts/bootstrap-laplace-runner.sh, which is the only place
# privileged setup lives.

bootstrap:
    sudo scripts/bootstrap-laplace-runner.sh bootstrap

bootstrap-status:
    sudo scripts/bootstrap-laplace-runner.sh status

bootstrap-reset:
    sudo scripts/bootstrap-laplace-runner.sh reset

# === One-shot host setup (Layer 0 + Layer 1 combined) ===
# Calls scripts/setup-host.sh which orchestrates bootstrap-laplace-runner.sh
# (Layer 0) and dotnet run --project Laplace.Migrations -- up (Layer 1) in
# sequence. Idempotent. Use this on a fresh host (or after a reset) instead
# of running bootstrap + db-up separately.

setup-host:
    scripts/setup-host.sh setup

setup-host-status:
    scripts/setup-host.sh status

setup-host-reset:
    scripts/setup-host.sh reset

# === Build ===
#
# Top-level CMake (per ADR 0032 Path B) drives engine + extension from
# one tree. `just build` is the canonical entry point. PG_PREFIX defaults
# to /usr/lib/postgresql/18 (stock); override via LAPLACE_PG_PREFIX env
# var to point at the custom build at /opt/laplace/pgsql-18.

# Build deps from external/ submodules (PROJ, GEOS, GDAL, PG, PostGIS,
# tree-sitter runtime) via the unified ExternalProject_Add pipeline at
# external/CMakeLists.txt per ADR 0038. gcc toolchain for the dep chain
# (icpx is reserved for the engine — gcc avoids PROJ's IntelLLVM-gated
# upstream bug and builds ~2-3x faster). Idempotent — stamp-based; re-runs
# with the same submodule SHAs are no-ops. One-time ~10-12 min on a clean
# /opt/laplace.
build-deps:
    cmake -B build/deps -S external
    cmake --build build/deps -j

# Build everything: 3 engine .so + 2 extension .so + 2 preprocessed SQL
# install scripts. One command, one ninja graph. Intel toolchain (icpx)
# applied via cmake/toolchains/intel-oneapi.cmake — earns its slot via
# MKL/Spectra/TBB in liblaplace_dynamics.
#
# LD_LIBRARY_PATH points gtest_discover_tests (which runs each test binary
# at build time to enumerate test names) at the freshly built engine
# liblaplace_*.so files. Without this the loader picks up any stale
# liblaplace_core.so left in /usr/local/lib by an old `sudo cmake --install`
# (via /etc/ld.so.cache), which causes "undefined symbol" failures the
# moment we add a new exported function to liblaplace_core.
build:
    cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
        -DLAPLACE_PG_PREFIX=${LAPLACE_PG_PREFIX:-/usr/lib/postgresql/18}
    LD_LIBRARY_PATH="$(pwd)/build/engine/core:$(pwd)/build/engine/dynamics:$(pwd)/build/engine/synthesis:${LD_LIBRARY_PATH:-}" cmake --build build

# Install engine .so + both extensions into the configured PG prefix.
# For stock PG that's /usr/lib/postgresql/18/{lib,share}; needs sudo.
# For custom PG at /opt/laplace/pgsql-18 (laplace-runner-owned), no sudo.
install: build
    sudo cmake --install build

# Same install path as `just install`, scoped for laplace-runner-owned
# /opt/laplace prefix (no sudo).
install-laplace-prefix: build
    cmake --install build

build-app:
    cd app && dotnet build Laplace.slnx -c Release

build-migrations:
    cd app && dotnet build Laplace.Migrations/Laplace.Migrations.csproj -c Release

build-perfcache:
    scripts/build-perfcache.sh

# Wipe build/ (idempotent re-run preserves /opt/laplace/* dep installs).
clean:
    rm -rf build

# === Launch ===

launch-db:
    sudo systemctl start postgresql 2>/dev/null || pg_ctlcluster 18 main start

# === Layer 1 — extension lifecycle (DbUp) ===
# Per ADR 0021 (DbUp + Npgsql) + ADR 0023 (extension owns schema).
# These wrap dotnet run --project app/Laplace.Migrations.

db-up: install
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- up

db-status:
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- status

# Drop SchemaVersions only — DbUp will re-apply migrations on next 'db-up'.
# Preserves the database, extensions, and substrate data.
db-reset:
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- reset

# Full Layer-1 wipe — DROP DATABASE laplace + re-create empty.
# Loses ALL substrate data. Run 'just db-up' afterward to rebuild.
db-nuke:
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- nuke

# Generate a new timestamped migration file in db/migrations/
migrate-new name:
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! "{{name}}" =~ ^[a-z][a-z0-9_]*$ ]]; then
        echo "✗ Migration name must be snake_case (a-z0-9_), starting with a letter."
        exit 1
    fi
    stamp=$(date -u +%Y%m%d%H%M%S)
    file="db/migrations/${stamp}_{{name}}.sql"
    cat > "$file" <<EOF
    -- Migration ${stamp}_{{name}}
    --
    -- TODO: describe the orchestration concern this migration addresses.
    -- Per ADR 0023, DbUp migrations orchestrate extension lifecycle and
    -- cross-extension setup — they do NOT define substrate schema. If you
    -- find yourself writing CREATE TABLE for laplace.* objects here, STOP
    -- and put it in extension/laplace--A.B.C--D.E.F.sql instead.
    --
    -- Write idempotent SQL (IF NOT EXISTS / DO \$\$ blocks).
    EOF
    echo "✓ Created $file"

# === Seed ===

seed-t0: build-perfcache
    scripts/seed-t0.sh

# === Setup (full convenience composite) ===

setup: launch-db db-up seed-t0
    @echo "Laplace ready. Try: just query 'SELECT laplace_version();'"

# === Ingest ===

ingest source path="":
    scripts/ingest-source.sh {{source}} {{path}}

# === Query / Cascade ===

query sql:
    psql -d laplace -c "{{sql}}"

cascade prompt:
    app/Laplace.Cli/bin/Release/net10.0/laplace-cli cascade --prompt "{{prompt}}"

# === Synthesis ===

synthesize recipe:
    app/Laplace.Cli/bin/Release/net10.0/laplace-cli synthesize --recipe {{recipe}}

roundtrip model_path:
    scripts/roundtrip.sh {{model_path}}

# === Verify ===

verify: verify-determinism verify-fk verify-perfcache

verify-determinism:
    scripts/verify-determinism.sh

verify-fk:
    psql -d laplace -f scripts/verify-fk.sql

verify-perfcache:
    scripts/verify-perfcache.sh

# === Status ===

status:
    @git log --oneline -10


# === Test ===
#
# Three test surfaces per STANDARDS.md Testing:
#   ctest        → engine C++ unit tests (GoogleTest, gtest_discover_tests)
#   pg_regress   → extension SQL tests (lands per-Chunk with the first real
#                  function under extension/{laplace_geom,laplace_substrate}/tests/)
#   dotnet test  → C# xUnit + Testcontainers (Migrations DbUp idempotency
#                  against a postgis/postgis:18 container)

test: test-engine test-app

# ctest runs everything discovered by gtest_discover_tests in each engine
# subdir. Requires `just build` first.
#
# Same LD_LIBRARY_PATH discipline as `build` — points ctest's test runs at
# the freshly built engine liblaplace_*.so files so stale system installs
# (e.g. /usr/local/lib left over from a prior `sudo cmake --install`)
# can't poison the loader.
test-engine:
    cd build && LD_LIBRARY_PATH="$(realpath engine/core):$(realpath engine/dynamics):$(realpath engine/synthesis):${LD_LIBRARY_PATH:-}" ctest --output-on-failure

# dotnet test covers all .Tests projects (Laplace.Engine.*.Tests +
# Laplace.Migrations.Tests). Testcontainers requires a running Docker
# daemon.
test-app:
    cd app && dotnet test Laplace.slnx -c Release
