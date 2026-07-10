# NOTE: Linux dev convenience layer only — recipes here may drift.
# The canonical orchestration paths are scripts/pipeline.sh (Linux CI)
# and scripts/win/*.cmd (Windows). scripts/validate-pipeline.py is the
# cross-toolchain policy gate; trust it over this file when they disagree.

set shell := ["bash", "-uc"]

export LAPLACE_DATA_ROOT := env_var_or_default("LAPLACE_DATA_ROOT", "/vault/Data")

# Parallelism defaults (override with env, or LAPLACE_TEST_SERIAL=1 for serial tests).
export CMAKE_BUILD_PARALLEL_LEVEL := env_var_or_default("CMAKE_BUILD_PARALLEL_LEVEL", `nproc 2>/dev/null || echo 1`)
export CTEST_PARALLEL_LEVEL := env_var_or_default("CTEST_PARALLEL_LEVEL", `nproc 2>/dev/null || echo 1`)

default:
    @just --list

check-prereqs:
    @scripts/check-prereqs.sh

bootstrap:
    sudo scripts/bootstrap-laplace-runner.sh bootstrap

bootstrap-status:
    sudo scripts/bootstrap-laplace-runner.sh status

bootstrap-reset:
    sudo scripts/bootstrap-laplace-runner.sh reset

setup-host:
    scripts/setup-host.sh setup

setup-host-status:
    scripts/setup-host.sh status

setup-host-reset:
    scripts/setup-host.sh reset

submodule-sanity:
    @scripts/normalize-submodule-attributes.sh

build-deps: submodule-sanity
    cmake -B build/deps -S external
    umask 0002 && cmake --build build/deps -j

verify-deps:
    @chmod +x scripts/verify-pg-postgis.sh
    @scripts/verify-pg-postgis.sh

# Incremental build (stamp-skips codegen when manifests unchanged).
build:
    bash scripts/pipeline.sh build

# Wipe build/ then full rebuild (force codegen + clean tree).
rebuild:
    bash scripts/pipeline.sh --force-rebuild --force-codegen build

# Keep configure, rebuild all objects.
build-clean-first:
    bash scripts/pipeline.sh --clean-first --force-codegen build

# Force codegen even when stamp is fresh.
build-force-codegen:
    bash scripts/pipeline.sh --force-codegen build

install: build
    bash scripts/pipeline.sh install

install-laplace-prefix: install

build-app:
    cd app && dotnet build Laplace.slnx -c Release

build-migrations:
    cd app && dotnet build Laplace.Migrations/Laplace.Migrations.csproj -c Release

build-perfcache:
    cmake --build build --target laplace_t0_perfcache

clean:
    bash scripts/pipeline.sh clean

clean-all: clean
    rm -rf app/*/bin app/*/obj web/node_modules web/dist 2>/dev/null || true
    @echo "cleaned build/ + app bin/obj + web node_modules/dist"

launch-db:
    sudo systemctl start laplace-postgresql.service

db-up: install
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- up

db-status:
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- status

db-reset:
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- reset

db-nuke:
    cd app && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- nuke

migrate-new name:
    set -euo pipefail
    if [[ ! "{{name}}" =~ ^[a-z][a-z0-9_]*$ ]]; then
        echo "Migration name must be snake_case (a-z0-9_), starting with a letter."
        exit 1
    fi
    stamp=$(date -u +%Y%m%d%H%M%S)
    file="db/migrations/${stamp}_{{name}}.sql"
    cat > "$file" <<EOF
    EOF
    echo "Created $file"
    echo "Layer-1 only — substrate objects belong in extension/laplace_substrate/sql/ (see its README.md)"

seed-t0: build-perfcache build-app
    cd app && dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- ingest unicode

db-fresh: build-perfcache build-app
    set -euo pipefail
    rm -f "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"/lib/postgresql/*/laplace_substrate.so \
          "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"/share/postgresql/*/extension/laplace_substrate*
    umask 0002
    cmake --install build/extension/laplace_substrate >/dev/null
    cd app
    dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- nuke --yes
    dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- up
    dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- ingest unicode
    echo "db-fresh: empty substrate + current extension + T0 seeded (consensus folded, layer-0 marker set)"

setup: launch-db db-up seed-t0
    @echo "Laplace ready. Try: just query 'SELECT laplace_version();'"

ingest source path="": build-app
    scripts/ingest-source.sh {{source}} {{path}}

e2e *models: build-app
    scripts/e2e-substrate.sh {{models}}

ingest-all: build-app
    scripts/ingest-source.sh all

decomposer-test source: build-app
    @chmod +x scripts/decomposer-test.sh scripts/decomposer-isolate.sh scripts/decomposer-ensure-floor.sh
    scripts/decomposer-test.sh {{source}}

decomposer-promote source: build-app
    @chmod +x scripts/decomposer-promote.sh
    scripts/decomposer-promote.sh {{source}}

decomposer-matrix *flags: build-app
    @chmod +x scripts/decomposer-matrix.sh scripts/decomposer-test.sh scripts/decomposer-promote.sh
    scripts/decomposer-matrix.sh {{flags}}

decomposer-isolate dbname:
    @chmod +x scripts/decomposer-isolate.sh
    scripts/decomposer-isolate.sh {{dbname}}

ingest-tinyllama: build-app
    scripts/ingest-source.sh safetensors "${LAPLACE_TINYLLAMA_DIR:?set LAPLACE_TINYLLAMA_DIR to the HF snapshot dir (config+tokenizer+weights)}"

audit-decomposers *args: build-app
    @chmod +x scripts/audit-decomposers.sh
    scripts/audit-decomposers.sh {{args}}

query sql:
    psql -h /var/run/postgresql -U laplace_admin -d "${LAPLACE_QUERY_DB:-laplace-dev}" -c "{{sql}}"

cascade prompt:
    app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli cascade --prompt "{{prompt}}"

synthesize subcommand *args: build-app
    cd app && LD_LIBRARY_PATH="$(pwd)/../build/engine/synthesis:$(pwd)/../build/engine/dynamics:$(pwd)/../build/engine/core:${LD_LIBRARY_PATH:-}" \
        dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- synthesize {{subcommand}} {{args}}

synthesize-tinyllama output="/tmp/tinyllama-substrate.gguf": build-app
    just synthesize substrate "${LAPLACE_TINYLLAMA_DIR:?set LAPLACE_TINYLLAMA_DIR}/config.json" {{output}}

model-synthesize model_path:
    scripts/model-synthesize-ci.sh {{model_path}}

model-synthesize-ci: build build-app
    @chmod +x scripts/model-synthesize-ci.sh
    scripts/model-synthesize-ci.sh

verify: verify-determinism verify-fk verify-perfcache

verify-determinism: build
    cmake --build build --target laplace_verify_perfcache_determinism

verify-fk:
    psql -d laplace -U laplace_admin -f scripts/verify-fk.sql

verify-perfcache: build
    cmake --build build --target laplace_t0_perfcache
    cd build && LD_LIBRARY_PATH="$(realpath engine/core):$(realpath engine/dynamics):$(realpath engine/synthesis):${LD_LIBRARY_PATH:-}" \
        ctest -R '^LaplaceCoreCodepointTable' --output-on-failure

status:
    @git log --oneline -10

anchor issue="":
    @scripts/agent-anchor.sh {{issue}}

issue n:
    @scripts/agent-anchor.sh {{n}}

# Parallel test gate (ctest || regress, then dotnet). Use test-serial to force old order.
test:
    @chmod +x scripts/test-parallel.sh
    bash scripts/test-parallel.sh

test-serial:
    @chmod +x scripts/test-parallel.sh
    bash scripts/test-parallel.sh --serial

test-no-docker: test-engine regress

test-engine:
    @chmod +x scripts/test-parallel.sh
    bash scripts/test-parallel.sh --engine

regress:
    @chmod +x scripts/test-parallel.sh
    bash scripts/test-parallel.sh --regress

test-app:
    @chmod +x scripts/test-parallel.sh
    bash scripts/test-parallel.sh --app

publish:
    bash scripts/pipeline.sh publish

publish-force-npm:
    bash deploy/linux/deploy.sh --force-npm
