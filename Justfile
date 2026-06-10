# Linux CI / dev-host recipes only. Windows (canonical): scripts/win/*.cmd —
# see .github/instructions/build-environment.instructions.md (never cmake --install to Program Files).
set shell := ["bash", "-uc"]

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

build:
    cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
        -DLAPLACE_PG_PREFIX=${LAPLACE_PG_PREFIX:-/usr/lib/postgresql/18} \
        -DCMAKE_INSTALL_PREFIX=${LAPLACE_INSTALL_PREFIX:-/opt/laplace} \
        -DLAPLACE_INSTALL_STAGED=${LAPLACE_INSTALL_STAGED:-ON}
    LD_LIBRARY_PATH="$(pwd)/build/engine/core:$(pwd)/build/engine/dynamics:$(pwd)/build/engine/synthesis:${LD_LIBRARY_PATH:-}" cmake --build build

install: build
    #!/usr/bin/env bash
    umask 0002
    cmake --install build

install-laplace-prefix: install

build-app:
    cd app && dotnet build Laplace.slnx -c Release

build-migrations:
    cd app && dotnet build Laplace.Migrations/Laplace.Migrations.csproj -c Release

build-perfcache:
    cmake --build build --target laplace_t0_perfcache

clean:
    rm -rf build

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
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ ! "{{name}}" =~ ^[a-z][a-z0-9_]*$ ]]; then
        echo "✗ Migration name must be snake_case (a-z0-9_), starting with a letter."
        exit 1
    fi
    stamp=$(date -u +%Y%m%d%H%M%S)
    file="db/migrations/${stamp}_{{name}}.sql"
    cat > "$file" <<EOF
    EOF
    echo "✓ Created $file"
    echo "⚠ Layer-1 only — substrate objects belong in extension/laplace_substrate/sql/ (see its README.md)"

seed-t0: build-perfcache build-app
    cd app && dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- ingest unicode

db-fresh: build-perfcache build-app
    #!/usr/bin/env bash
    set -euo pipefail
    rm -f "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"/lib/postgresql/*/laplace_substrate.so \
          "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"/share/postgresql/*/extension/laplace_substrate*
    umask 0002
    cmake --install build/extension/laplace_substrate >/dev/null
    cd app
    dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- nuke --yes
    dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -c Release -- up
    dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- ingest unicode
    echo "✓ db-fresh: empty substrate + current extension + T0 seeded (consensus folded, layer-0 marker set)"

setup: launch-db db-up seed-t0
    @echo "Laplace ready. Try: just query 'SELECT laplace_version();'"

ingest source path="": build-app
    scripts/ingest-source.sh {{source}} {{path}}

e2e *models: build-app
    scripts/e2e-substrate.sh {{models}}

ingest-all: build-app
    scripts/ingest-source.sh all

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
    scripts/model-synthesize.sh {{model_path}}

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


test: test-engine regress test-app

test-no-docker: test-engine regress

test-engine:
    cd build && LD_LIBRARY_PATH="$(realpath engine/core):$(realpath engine/dynamics):$(realpath engine/synthesis):${LD_LIBRARY_PATH:-}" ctest --output-on-failure -LE regress

regress:
    cd build && ctest --output-on-failure -L regress

test-app:
    cd app && dotnet test Laplace.slnx -c Release
