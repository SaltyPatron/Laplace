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

# === Build ===

build: build-engine build-extension build-app

build-engine:
    # CMake picks compiler from PATH (CC/CXX env). With oneAPI's setvars.sh
    # sourced, icx/icpx are preferred. Without it, gcc/g++ is fine.
    cd engine && cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
    cd engine && cmake --build build

build-extension:
    cd extension && make USE_PGXS=1 PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config

install-extension: build-extension
    cd extension && sudo make USE_PGXS=1 PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config install

build-app:
    cd app && dotnet build Laplace.slnx -c Release

build-migrations:
    cd app && dotnet build Laplace.Migrations/Laplace.Migrations.csproj -c Release

build-perfcache:
    scripts/build-perfcache.sh

# === Launch ===

launch-db:
    sudo systemctl start postgresql 2>/dev/null || pg_ctlcluster 18 main start

# === Layer 1 — extension lifecycle (DbUp) ===
# Per ADR 0021 (DbUp + Npgsql) + ADR 0023 (extension owns schema).
# These wrap dotnet run --project app/Laplace.Migrations.

db-up: install-extension
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
    @cat .agent/status/STATE.md 2>/dev/null || echo "(STATE.md not yet initialized)"
    @echo ""
    @echo "=== Open blockers ==="
    @cat .agent/status/blockers.md 2>/dev/null || echo "(none)"

# === Test ===

test: test-engine test-extension test-app test-integration

test-engine:
    cd engine && cmake --build build --target test
    cd engine/build && ctest --output-on-failure

test-extension:
    cd extension && make installcheck PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config

test-app:
    cd app && dotnet test -c Release

test-integration:
    scripts/test-integration.sh

# === Clean ===

clean: clean-engine clean-extension clean-app

clean-engine:
    rm -rf engine/build

clean-extension:
    cd extension && make clean 2>/dev/null || true

clean-app:
    cd app && dotnet clean 2>/dev/null || true
