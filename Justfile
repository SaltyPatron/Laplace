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

# === Build ===

build: build-engine build-extension build-app

build-engine:
    cd engine && cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icpx
    cd engine && cmake --build build

build-extension:
    cd extension && make USE_PGXS=1 PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config

install-extension: build-extension
    cd extension && sudo make USE_PGXS=1 PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config install

build-app:
    cd app && dotnet build -c Release

build-perfcache:
    scripts/build-perfcache.sh

# === Launch / Setup ===

launch-db:
    sudo systemctl start postgresql 2>/dev/null || pg_ctlcluster 18 main start

create-db:
    sudo -u postgres createdb laplace
    sudo -u postgres psql -d laplace -c "CREATE EXTENSION postgis;"
    sudo -u postgres psql -d laplace -c "CREATE EXTENSION laplace;"

apply-schema:
    psql -d laplace -f extension/schema.sql

seed-t0: build-perfcache
    scripts/seed-t0.sh

setup: launch-db install-extension create-db apply-schema seed-t0
    @echo "Laplace ready. Try: just query 'SELECT count(*) FROM entities;'"

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

nuke-db:
    @read -p "DROP database laplace? [y/N] " confirm && [[ "$$confirm" == "y" ]] || (echo "Aborted." && exit 1)
    sudo -u postgres dropdb laplace
