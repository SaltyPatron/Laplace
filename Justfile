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
# (three-layer architecture) + (laplace-runner).
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
# Top-level CMake ( Path B) drives engine + extension from
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
# external/CMakeLists.txt. gcc toolchain for the dep chain
# (icpx is reserved for the engine — gcc avoids PROJ's IntelLLVM-gated
# upstream bug and builds ~2-3x faster). Idempotent — stamp-based; re-runs
# with the same submodule SHAs are no-ops. One-time ~10-12 min on a clean
# /opt/laplace.
#
# Depends on submodule-sanity so the CRLF-fixture override fix runs
# before any build artifact gets pinned to dirty source state.
build-deps: submodule-sanity
    cmake -B build/deps -S external
    umask 0002 && cmake --build build/deps -j

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
    #!/usr/bin/env bash
    # umask 0002: every dir this install creates under the shared prefix is
    # born group-writable (g+w; setgid inherits from /opt/laplace's 2775), so
    # the OTHER identity (CI laplace-runner vs developer) can always pre-wipe
    # and overwrite. Component installs bypass the root pre-wipe/enforcer, so
    # creation-time correctness is the only shape that covers every path.
    umask 0002
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
    sudo systemctl start laplace-postgresql.service

# === Layer 1 — extension lifecycle (DbUp) ===
# (DbUp + Npgsql) + (extension owns schema).
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
    --, DbUp migrations orchestrate extension lifecycle and
    -- cross-extension setup — they do NOT define substrate schema. If you
    -- find yourself writing CREATE TABLE for laplace.* objects here, STOP
    -- and put it in extension/laplace--A.B.C--D.E.F.sql instead.
    --
    -- Write idempotent SQL (IF NOT EXISTS / DO \$\$ blocks).
    EOF
    echo "✓ Created $file"

# === Seed ===

# T0 DB seed: UnicodeDecomposer through THE ONE ingest path (IngestRunner +
# ConsensusAccumulatingWriter) — consensus folds at ingest and the layer-0
# HasLayerCompleted marker is written, so the ladder can proceed. The legacy
# `seed-unicode` plain-writer command (no consensus fold, no marker — every
# script had to work around it) is deleted.
seed-t0: build-perfcache build-app
    cd app && dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- ingest unicode

# Fresh DB for clean testing. Re-ingesting a model is refused by design (it would
# double-count its votes and contaminate consensus), so any test that re-runs
# ingestion must reset the whole DB first — never bypass the guard. This drops +
# recreates the database, reinstalls the current substrate extension (extension-only
# install, to avoid the perfcache-file perms issue in the full `install` target),
# re-applies migrations (CREATE EXTENSION loads the current SQL), and re-seeds T0.
db-fresh: build-perfcache build-app
    #!/usr/bin/env bash
    set -euo pipefail
    # No checkpoint to clear: ingestion is idempotent by the substrate's own law
    # (content-addressing + ON CONFLICT DO NOTHING), so a re-run converges with no
    # side journal (IngestRunner). Drop + recreate the DB, reinstall the current
    # substrate extension (extension-only install, to avoid the perfcache-file perms
    # issue in the full `install` target), re-apply migrations (CREATE EXTENSION loads
    # the current SQL), and re-seed T0.
    # Component installs skip the root pre-wipe; a leftover .so from a prior
    # install state makes cmake's file(RPATH_CHANGE) bail (half-rewritten
    # RUNPATH). Clear the artifacts first — same remedy as the root pre-wipe;
    # the install recreates them fresh, owned by this installer.
    rm -f "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"/lib/postgresql/*/laplace_substrate.so \
          "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"/share/postgresql/*/extension/laplace_substrate*
    umask 0002    # dirs born g+w — see the `install` recipe
    cmake --install build/extension/laplace_substrate >/dev/null
    cd app
    echo NUKE | dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- nuke
    dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- up
    dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- ingest unicode
    echo "✓ db-fresh: empty substrate + current extension + T0 seeded (consensus folded, layer-0 marker set)"

# === Setup (full convenience composite) ===

setup: launch-db db-up seed-t0
    @echo "Laplace ready. Try: just query 'SELECT laplace_version();'"

# === Ingest ===

ingest source path="": build-app
    scripts/ingest-source.sh {{source}} {{path}}

# End-to-end substrate rebuild on laplace-dev (DESTRUCTIVE: db-fresh first):
# seeds (unicode + iso639) then each model one at a time, consensus folding at
# every period end, audit last. Models default to TinyLlama + Phi-2; pass
# explicit model dirs to override. LAPLACE_E2E_DB overrides the target DB.
e2e *models: build-app
    scripts/e2e-substrate.sh {{models}}

# Ingest the whole seed/lexical ladder in dependency order:
#   unicode → iso639 → wordnet → omw → ud → tatoeba → atomic2020 → conceptnet → wiktionary
# Each source's IngestRunner layer check gates on the lower layers; the layer-4
# corpora are independent. Idempotent — re-runs short-circuit via content-addressing + ON CONFLICT.
ingest-all: build-app
    scripts/ingest-source.sh all

# Model dir by convention: $LAPLACE_TINYLLAMA_DIR (never a hardcoded snapshot SHA).
ingest-tinyllama: build-app
    scripts/ingest-source.sh model "${LAPLACE_TINYLLAMA_DIR:?set LAPLACE_TINYLLAMA_DIR to the model snapshot dir}"

# End-to-end decomposer audit: runs the lexical ladder on the current DB, asserts the seed
# decomposers land CONTENT physicalities (not just attestations — the pre-fix regression),
# and reports before/after entity/physicality/attestation counts. Flags: --full (also the
# big tatoeba/conceptnet/wiktionary corpora), --fresh (db-nuke first), --from <src> (resume).
audit-decomposers *args: build-app
    @chmod +x scripts/audit-decomposers.sh
    scripts/audit-decomposers.sh {{args}}

# === Query / Cascade ===

query sql:
    psql -h /var/run/postgresql -U laplace_admin -d "${LAPLACE_QUERY_DB:-laplace}" -c "{{sql}}"

cascade prompt:
    app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli cascade --prompt "{{prompt}}"

# === Synthesis ===

# Generic synthesize entrypoint (`substrate` — re-export = fill the mold from
# substrate consensus).
# Usage:
#   just synthesize substrate /path/to/recipe.json /tmp/out.gguf
synthesize subcommand *args: build-app
    cd app && LD_LIBRARY_PATH="$(pwd)/../build/engine/synthesis:$(pwd)/../build/engine/dynamics:$(pwd)/../build/engine/core:${LD_LIBRARY_PATH:-}" \
        dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- synthesize {{subcommand}} {{args}}

# Substrate synthesis using the TinyLlama recipe as the mold (model dir by
# convention: $LAPLACE_TINYLLAMA_DIR — never a hardcoded snapshot SHA).
# Pass a different recipe.json to export the same substrate data at a different dimension.
synthesize-tinyllama output="/tmp/tinyllama-substrate.gguf": build-app
    just synthesize substrate "${LAPLACE_TINYLLAMA_DIR:?set LAPLACE_TINYLLAMA_DIR}/config.json" {{output}}

model-synthesize model_path:
    scripts/model-synthesize.sh {{model_path}}

# Substrate model CI: ingest model → evidence/consensus → synthesize from substrate → GGUF.
# Recipe-agnostic — the synthesis target_dim comes from whatever recipe.json is passed,
# not from the ingested model shape.
model-synthesize-ci: build build-app
    @chmod +x scripts/model-synthesize-ci.sh
    scripts/model-synthesize-ci.sh

# === Verify ===

verify: verify-determinism verify-fk verify-perfcache

verify-determinism: build
    cmake --build build --target laplace_verify_perfcache_determinism

verify-fk:
    psql -d laplace -U laplace_admin -f scripts/verify-fk.sql

verify-perfcache: build
    cmake --build build --target laplace_t0_perfcache
    cd build && LD_LIBRARY_PATH="$(realpath engine/core):$(realpath engine/dynamics):$(realpath engine/synthesis):${LD_LIBRARY_PATH:-}" \
        ctest -R '^LaplaceCoreCodepointTable' --output-on-failure

# === Status ===

status:
    @git log --oneline -10

# === Agent context (SSH / compressed-context workflows) ===
#
# Compact briefing: issue body, git head, hard stops, prereq summary.
# Issue defaults to ISSUE= in .laplace-session (see .laplace-session.example).

anchor issue="":
    @scripts/agent-anchor.sh {{issue}}

issue n:
    @scripts/agent-anchor.sh {{n}}


# === Test ===
#
# Three test surfaces per the testing standard:
#   ctest        → engine C++ unit tests (GoogleTest, gtest_discover_tests)
#   pg_regress   → extension SQL tests (lands per-Chunk with the first real
#                  function under extension/{laplace_geom,laplace_substrate}/tests/)
#   dotnet test  → C# xUnit + Testcontainers (Migrations DbUp idempotency
#                  against a postgis/postgis:18 container)

test: test-engine regress test-app

# Engine + pg_regress only — no Testcontainers / Docker (for agents when docker is off).
test-no-docker: test-engine regress

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
