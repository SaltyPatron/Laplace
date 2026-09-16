# Linux developer convenience. scripts/pipeline.sh owns build/install/runtime
# sequencing; scripts/test-parallel.sh executes tests directly.

set shell := ["bash", "-uc"]

export LAPLACE_DATA_ROOT := env_var_or_default("LAPLACE_DATA_ROOT", "/vault/Data")
export CMAKE_BUILD_PARALLEL_LEVEL := env_var_or_default("CMAKE_BUILD_PARALLEL_LEVEL", `nproc 2>/dev/null || echo 1`)
export CTEST_PARALLEL_LEVEL := env_var_or_default("CTEST_PARALLEL_LEVEL", `nproc 2>/dev/null || echo 1`)

default:
    @just --list

check-prereqs:
    @scripts/check-prereqs.sh

bootstrap:
    @echo "Use: sudo bash scripts/setup-host.sh"
    @exit 2

bootstrap-status:
    sudo bash scripts/setup-host.sh status

bootstrap-reset:
    sudo bash scripts/setup-host.sh reset

setup-host:
    sudo bash scripts/setup-host.sh setup

setup-host-status:
    sudo bash scripts/setup-host.sh status

setup-host-reset:
    sudo bash scripts/setup-host.sh reset

build-deps:
    bash scripts/build-system-deps.sh

verify-deps:
    @chmod +x scripts/verify-pg-postgis.sh
    @scripts/verify-pg-postgis.sh

build:
    bash scripts/pipeline.sh build

rebuild:
    bash scripts/pipeline.sh --force-rebuild --force-codegen build

build-clean-first:
    bash scripts/pipeline.sh --clean-first --force-codegen build

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

db-up:
    scripts/db-migrations.sh up

db-status:
    scripts/db-migrations.sh status

db-reset:
    scripts/db-migrations.sh reset

db-nuke:
    scripts/db-migrations.sh nuke

migrate-new name:
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! "{{name}}" =~ ^[a-z][a-z0-9_]*$ ]]; then
        echo "Migration name must be snake_case (a-z0-9_), starting with a letter."
        exit 1
    fi
    stamp=$(date -u +%Y%m%d%H%M%S)
    file="db/migrations/${stamp}_{{name}}.sql"
    : > "$file"
    echo "Created $file"

seed-t0: build
    scripts/ingest-source.sh unicode

db-fresh:
    set -euo pipefail
    scripts/db-migrations.sh nuke --yes
    scripts/db-migrations.sh up
    bash scripts/pipeline.sh sync-extension tune-pg tune-laplace perfcache-guc
    bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
    echo "db-fresh: database recreated from the prepared runtime; no build or ingest was performed"

db-fresh-foundation: db-fresh seed-t0

setup: launch-db db-up seed-t0
    @echo "Laplace ready. Try: just query 'SELECT laplace_version();'"

ingest source path="": build
    scripts/ingest-source.sh {{source}} {{path}}

ingest-all: build
    scripts/ingest-source.sh all

e2e *models: build
    scripts/e2e-substrate.sh {{models}}

eval:
    bash scripts/test-parallel.sh --app-live

decomposer-test source: build
    scripts/decomposer-test.sh {{source}}

decomposer-promote source: build
    scripts/decomposer-promote.sh {{source}}

decomposer-matrix *flags: build
    scripts/decomposer-matrix.sh {{flags}}

decomposer-isolate dbname:
    scripts/decomposer-isolate.sh {{dbname}}

ingest-tinyllama: build
    scripts/ingest-source.sh safetensors "${LAPLACE_TINYLLAMA_DIR:?set LAPLACE_TINYLLAMA_DIR to the HF snapshot dir}"

audit-decomposers *args: build
    scripts/audit-decomposers.sh {{args}}

audit-source-fidelity:
    scripts/audit-semantic-source-fidelity.sh

query sql:
    psql -h /var/run/postgresql -U laplace_admin -d "${LAPLACE_QUERY_DB:-laplace}" -c "{{sql}}"

cascade prompt: build
    scripts/laplace cascade --prompt "{{prompt}}"

synthesize subcommand *args: build
    scripts/laplace synthesize {{subcommand}} {{args}}

synthesize-tinyllama output="/build/laplace/work/model-outputs/tinyllama-substrate.gguf": build
    mkdir -p "$(dirname '{{output}}')"
    scripts/laplace synthesize substrate "${LAPLACE_TINYLLAMA_DIR:?set LAPLACE_TINYLLAMA_DIR}/config.json" {{output}}

model-synthesize model_path:
    scripts/model-synthesize-ci.sh {{model_path}}

model-synthesize-ci: build
    scripts/model-synthesize-ci.sh

verify:
    bash scripts/test-parallel.sh --engine

verify-determinism:
    bash scripts/test-parallel.sh --engine

verify-fk:
    bash scripts/test-parallel.sh --regress

verify-perfcache:
    bash scripts/test-parallel.sh --engine

status:
    @git log --oneline -10

anchor issue="":
    @scripts/agent-anchor.sh {{issue}}

issue n:
    @scripts/agent-anchor.sh {{n}}

test:
    bash scripts/test-parallel.sh

test-serial:
    bash scripts/test-parallel.sh --serial

test-no-docker: test-engine regress

test-engine:
    bash scripts/test-parallel.sh --engine

regress:
    bash scripts/test-parallel.sh --regress

test-app:
    bash scripts/test-parallel.sh --app

publish:
    bash scripts/publish-applications.sh deploy

publish-force-npm:
    LAPLACE_FORCE_NPM=1 bash scripts/publish-applications.sh deploy
