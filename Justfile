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

# Apply local-only attribute overrides that suppress text-normalization
# of upstream CRLF test fixtures in 5 of 303 tree-sitter grammars (awk,
# bash, c, djot, jsdoc). Without this prereq, every fresh `git clone
# --recursive` leaves those submodules DIRTY after checkout — see
# scripts/normalize-submodule-attributes.sh for full rationale.
# Idempotent: silent no-op on clean state.
submodule-sanity:
    @scripts/normalize-submodule-attributes.sh

# Build deps from external/ submodules (PROJ, GEOS, GDAL, PG, PostGIS,
# tree-sitter runtime) via the unified ExternalProject_Add pipeline at
# external/CMakeLists.txt per ADR 0038. gcc toolchain for the dep chain
# (icpx is reserved for the engine — gcc avoids PROJ's IntelLLVM-gated
# upstream bug and builds ~2-3x faster). Idempotent — stamp-based; re-runs
# with the same submodule SHAs are no-ops. One-time ~10-12 min on a clean
# /opt/laplace.
#
# Depends on submodule-sanity so the CRLF-fixture override fix runs
# before any build artifact gets pinned to dirty source state.
build-deps: submodule-sanity
    cmake -B build/deps -S external
    cmake --build build/deps -j

# Verify /opt/laplace/pgsql-18 + PostGIS extension artifacts (and SQL smoke
# when the substrate cluster is up). Run after build-deps or bootstrap.
verify-deps:
    @chmod +x scripts/verify-pg-postgis.sh
    @scripts/verify-pg-postgis.sh

# Build everything: 3 engine .so + 2 extension .so + 2 preprocessed SQL
# install scripts. One command, one ninja graph. Intel toolchain (icpx)
# applied via cmake/toolchains/intel-oneapi.cmake — earns its slot via
# MKL/Spectra/TBB in liblaplace_dynamics.
#
# Iterative defaults (per `sudo just bootstrap` host setup):
#   CMAKE_INSTALL_PREFIX=/opt/laplace     (laplace-runner-owned, sudo-free)
#   LAPLACE_INSTALL_STAGED=ON             (extensions land in
#                                          /opt/laplace/{lib,share}/postgresql/$PG_MAJOR;
#                                          PG finds them via the conf
#                                          paths set in bootstrap_pg_extension_paths)
#   LAPLACE_PG_PREFIX=/usr/lib/postgresql/18
#                                         (system PG; override only when
#                                          a custom /opt/laplace/pgsql-18
#                                          is built via `just build-deps`)
#
# Override any of these via env var for CI or special-purpose builds:
#   LAPLACE_INSTALL_PREFIX=/usr/local LAPLACE_INSTALL_STAGED=OFF just build
#
# LD_LIBRARY_PATH points gtest_discover_tests (which runs each test binary
# at build time to enumerate test names) at the freshly built engine
# liblaplace_*.so files. Without this the loader can pick up any stale
# liblaplace_core.so left in /usr/local/lib by an old `sudo cmake --install`
# (via /etc/ld.so.cache), which causes "undefined symbol" failures when a
# new exported function is added.
build:
    cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
        -DLAPLACE_PG_PREFIX=${LAPLACE_PG_PREFIX:-/usr/lib/postgresql/18} \
        -DCMAKE_INSTALL_PREFIX=${LAPLACE_INSTALL_PREFIX:-/opt/laplace} \
        -DLAPLACE_INSTALL_STAGED=${LAPLACE_INSTALL_STAGED:-ON}
    LD_LIBRARY_PATH="$(pwd)/build/engine/core:$(pwd)/build/engine/dynamics:$(pwd)/build/engine/synthesis:${LD_LIBRARY_PATH:-}" cmake --build build

# Install engine .so + both extensions to /opt/laplace — sudo-free.
# Per `sudo just bootstrap`:
#   /opt/laplace is laplace-runner-owned (writable by ahart + laplace-runner)
#   /opt/laplace/lib is registered with the dynamic linker (ld.so.cache)
#   The running PG knows to look in /opt/laplace/{share,lib}/postgresql/18
#   for extension control + .so files (via extension_control_path +
#   dynamic_library_path GUCs).
# That means CREATE EXTENSION laplace_geom and CREATE EXTENSION
# laplace_substrate find everything immediately after this completes.
install: build
    cmake --install build

# Legacy alias — both targets now do the same sudo-free install. Kept so
# existing CI / docs that reference `install-laplace-prefix` keep working.
install-laplace-prefix: install

build-app:
    cd app && dotnet build Laplace.slnx -c Release

build-migrations:
    cd app && dotnet build Laplace.Migrations/Laplace.Migrations.csproj -c Release

# Build just the T0 perf-cache blob (UCDXML + DUCET -> binary). Part of the
# normal engine build; this target iterates it standalone. Requires `just
# build` to have configured build/ first.
build-perfcache:
    cmake --build build --target laplace_t0_perfcache

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

# T0 DB seed: UnicodeDecomposer → NpgsqlSubstrateWriter (ADR 0006 sibling of
# build-perfcache). Replaces deleted scripts/seed-t0.sh per ADR 0053.
seed-t0: build-perfcache build-app
    cd app && dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- seed-unicode

# === Setup (full convenience composite) ===

setup: launch-db db-up seed-t0
    @echo "Laplace ready. Try: just query 'SELECT laplace_version();'"

# === Ingest ===

ingest source path="": build-app
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
    psql -d laplace -U laplace_admin -f scripts/verify-fk.sql

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

test: test-engine regress test-app

# ctest runs everything discovered by gtest_discover_tests in each engine
# subdir. Excludes pg_regress tests (those are `just regress`). Requires
# `just build` first.
#
# Same LD_LIBRARY_PATH discipline as `build` — points ctest's test runs at
# the freshly built engine liblaplace_*.so files so stale system installs
# (e.g. /usr/local/lib left over from a prior `sudo cmake --install`)
# can't poison the loader.
test-engine:
    cd build && LD_LIBRARY_PATH="$(realpath engine/core):$(realpath engine/dynamics):$(realpath engine/synthesis):${LD_LIBRARY_PATH:-}" ctest --output-on-failure -LE regress

# pg_regress smoke tests for the laplace_geom + laplace_substrate extensions
# (per B′.9 #196). Drops + recreates per-extension test DBs
# (laplace_regress_geom, laplace_regress_substrate) as laplace_admin (peer
# auth), runs pg_regress against the running system PG with the staged
# extensions installed at /opt/laplace, diffs stdout against
# extension/<name>/tests/expected/*.out. Requires `just install` first so
# the staged extensions are in place. CTest fixtures sequence the setup /
# regress / teardown around the run.
regress:
    cd build && ctest --output-on-failure -L regress

# dotnet test covers all .Tests projects (Laplace.Engine.*.Tests +
# Laplace.Migrations.Tests). Testcontainers requires a running Docker
# daemon.
test-app:
    cd app && dotnet test Laplace.slnx -c Release
