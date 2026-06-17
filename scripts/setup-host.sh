#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BOOTSTRAP="$SCRIPT_DIR/bootstrap-laplace-runner.sh"
STRIPE_BOOTSTRAP="$SCRIPT_DIR/bootstrap-stripe-dev.sh"
MIGRATIONS_PROJ="$REPO_DIR/app/Laplace.Migrations/Laplace.Migrations.csproj"
RUNNER_USER="laplace-runner"

MODE="${1:-setup}"

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "============================================================"; echo "  $1"; echo "============================================================"; }

usage() {
    cat <<EOF
Usage: $0 <mode>

Modes:
  setup     (default) Full Layer 0 + 0.5 + 1 set-up. Idempotent.
            Layer 0 (sudo): bootstrap-laplace-runner.sh bootstrap — system
                            account, runner, PG roles, peer auth, sudoers,
                            ld.so.conf.d/laplace.conf, postgresql.conf
                            extension paths.
            Layer 0.5     : cmake --build build/deps — builds vendor deps
                            (proj/geos/gdal/pg/postgis/tree-sitter) into
                            /opt/laplace (no sudo, idempotent).
            Layer 1       : cmake --install build (extensions to /opt/laplace),
                            then dotnet run Laplace.Migrations -- up
                            (DbUp EnsureDatabase + CREATE EXTENSION + grants).

  status    Print Layer 0 + Layer 1 state. No mutations.

  reset     Tear down Layer 1 (db-nuke) then Layer 0 (bootstrap-laplace-runner.sh
            reset). Requires typing 'RESET' to confirm.

  layer0    Layer-0 only (system + runner + PG roles + auth + paths).
  deps      Layer-0.5 only (build vendor deps into /opt/laplace).
  layer1    Layer-1 only (cmake --install + DbUp).
    stripe    Stripe sandbox setup:
                        - run scripts/bootstrap-stripe-dev.sh for local user env
                        - run sudo bootstrap-laplace-runner.sh stripe for runner .env

After 'setup' completes the host is ready. Trigger CI:
  gh workflow run integration.yml
  (or push to main)
EOF
}

ensure_dotnet_present() {
    if ! command -v dotnet >/dev/null 2>&1; then
        red "dotnet not found in PATH. Install .NET 10 SDK first."
        exit 1
    fi
}

layer1_clean_foreign_build_artifacts() {
    local proj
    for proj in "$REPO_DIR"/app/Laplace.*; do
        [ -d "$proj" ] || continue
        ls "$proj"/*.csproj >/dev/null 2>&1 || continue
        for dir in obj bin; do
            local d="$proj/$dir"
            if [ -e "$d" ] && [ "$(stat -c '%U' "$d")" != "$RUNNER_USER" ]; then
                sudo rm -rf "$d"
                yellow "  - removed $d (was not owned by $RUNNER_USER)"
            fi
            if [ ! -e "$d" ]; then
                sudo install -d -o "$RUNNER_USER" -g "$RUNNER_USER" -m 775 "$d"
                yellow "  - created $d owned by $RUNNER_USER"
            fi
        done
    done
    return 0
}

runner_dotnet() {
    sudo -u "$RUNNER_USER" -H \
        PATH="$PATH" \
        DOTNET_NOLOGO=1 \
        DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        bash -c "cd '$REPO_DIR/app' && dotnet $*"
}

layer1_up() {
    say "Layer 1 — Build + run Laplace.Migrations as $RUNNER_USER"
    layer1_clean_foreign_build_artifacts
    runner_dotnet "run --project '$MIGRATIONS_PROJ' -c Release -- up"
    green "✓ Layer 1 ready: laplace DB + extensions + grants"
}

layer1_status() {
    say "Layer 1 — DbUp status"
    layer1_clean_foreign_build_artifacts
    runner_dotnet "run --project '$MIGRATIONS_PROJ' -c Release -- status" \
        || yellow "(database not yet present; run setup or layer1)"
}

layer1_nuke() {
    say "Layer 1 — DbUp nuke (DROP DATABASE laplace + recreate empty)"
    layer1_clean_foreign_build_artifacts
    runner_dotnet "run --project '$MIGRATIONS_PROJ' -c Release -- nuke"
}

layer1_build_install_extensions() {
    say "Layer 1 — Build + install laplace_geom + laplace_substrate extensions"
    (cd "$REPO_DIR" && cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DLAPLACE_INSTALL_STAGED=ON \
        -DCMAKE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}" \
        -DLAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-/usr/lib/postgresql/18}" | tail -3)
    (cd "$REPO_DIR" && cmake --build build | tail -3)
    (cd "$REPO_DIR" && cmake --install build --prefix "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}" | tail -3)
    green "✓ laplace_geom + laplace_substrate .so/.control/.sql installed at ${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
}

layer0_5_build_deps() {
    say "Layer 0.5 — Build vendor deps (proj/geos/gdal/pg/postgis/tree-sitter) into /opt/laplace"
    local deps_build_dir="/opt/laplace/build/deps"
    sudo install -d -o "$RUNNER_USER" -g "$RUNNER_USER" -m 2775 /opt/laplace/build "$deps_build_dir"
    sudo -u "$RUNNER_USER" -H \
        PATH="$PATH" \
        bash -c "cd '$REPO_DIR' && cmake -B '$deps_build_dir' -S external" 2>&1 | tail -5
    sudo -u "$RUNNER_USER" -H \
        PATH="$PATH" \
        bash -c "cd '$REPO_DIR' && cmake --build '$deps_build_dir' -j" 2>&1 | tail -10
    green "✓ Vendor deps built into /opt/laplace"
}

do_setup() {
    ensure_dotnet_present
    say "Layer 0 — System account, runner, PG roles, peer auth, sudoers, DB, postgis"
    sudo "$BOOTSTRAP" bootstrap
    layer0_5_build_deps
    layer1_build_install_extensions
    layer1_up

    say "DONE — host is set up."
    cat <<EOF

Layer 0 ✓ (bootstrap-laplace-runner.sh)
Layer 1 ✓ (DbUp: EnsureDatabase('laplace') + CREATE EXTENSION postgis + laplace + grants)

What's running:
  - laplace-runner system account
  - GitHub Actions runner: actions.runner.SaltyPatron-Laplace.hart-server.service
  - PostgreSQL database 'laplace' owned by laplace_admin
  - Extensions present in laplace DB: postgis, laplace

Next:
  gh workflow run integration.yml
  (or push to main to trigger CI)

Reset paths (each independently resettable):
  $0 reset                                   
  sudo $BOOTSTRAP reset                      
  cd $REPO_DIR && just db-nuke               
  cd $REPO_DIR && just db-reset              
EOF
}

do_status() {
    say "Layer 0 — bootstrap status"
    sudo "$BOOTSTRAP" status
    if id -u "$RUNNER_USER" >/dev/null 2>&1; then
        ensure_dotnet_present
        layer1_status
    else
        yellow "(laplace-runner not present yet — Layer 0 hasn't run)"
    fi
}

do_reset() {
    cat <<EOF
========================================================================
RESET — Tear down EVERYTHING this host provides for Laplace
========================================================================

This will:
  Layer 1: DROP DATABASE laplace (loses all substrate data)
  Layer 0: Deregister + remove the GitHub Actions runner,
           drop PG roles laplace_admin / laplace_app / laplace_readonly,
           strip pg_hba/pg_ident entries, remove sudoers entry,
           delete /var/lib/laplace-runner, remove the laplace-runner
           system account.

EOF
    read -rp "Type 'RESET' to confirm: " confirm
    if [ "$confirm" != "RESET" ]; then
        yellow "Aborted."
        exit 1
    fi

    if id -u "$RUNNER_USER" >/dev/null 2>&1 && command -v dotnet >/dev/null 2>&1; then
        say "Layer 1 — drop laplace database"
        sudo -u "$RUNNER_USER" -H \
            PATH="$PATH" \
            DOTNET_NOLOGO=1 \
            bash -c "cd '$REPO_DIR/app' && dotnet run --project '$MIGRATIONS_PROJ' -c Release -- nuke --yes" \
            || yellow "Layer 1 nuke skipped (database absent or unreachable)"
    fi

    say "Layer 0 — bootstrap-laplace-runner.sh reset"
    echo "RESET" | sudo "$BOOTSTRAP" reset

    green "===== HOST RESET COMPLETE ====="
}

do_stripe() {
    say "Stripe sandbox bootstrap (local user + runner)"

    if [ ! -x "$STRIPE_BOOTSTRAP" ]; then
        red "Missing executable script: $STRIPE_BOOTSTRAP"
        exit 1
    fi

    "$STRIPE_BOOTSTRAP" --persist-zsh
    sudo "$BOOTSTRAP" stripe
    green "✓ Stripe sandbox setup complete"
}

case "$MODE" in
    setup)
        do_setup
        ;;
    status)
        do_status
        ;;
    reset)
        do_reset
        ;;
    layer0)
        sudo "$BOOTSTRAP" bootstrap
        ;;
    deps|layer0.5)
        layer0_5_build_deps
        ;;
    layer1)
        ensure_dotnet_present
        layer1_build_install_extensions
        layer1_up
        ;;
    stripe)
        do_stripe
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        red "Unknown mode: $MODE"
        usage
        exit 64
        ;;
esac
