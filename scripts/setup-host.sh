#!/bin/bash
# scripts/setup-host.sh
#
# THE admin entrypoint for hart-server (or any host that runs the Laplace
# CI agent + Layer-1 substrate). Idempotent. One command does Layer 0
# (sudo-required system setup) + Layer 1 (DbUp: ensure database, install
# extensions, apply role grants). Layer 2 (build / test) is left for the
# Justfile + CI so this stays focused on "make this host ready."
#
# Usage:
#   scripts/setup-host.sh                  Default: full set-up (bootstrap + db-up)
#   scripts/setup-host.sh status           Print state of both layers
#   scripts/setup-host.sh reset             FULL teardown (Layer 1 then Layer 0)
#                                          Requires typing 'RESET' to confirm.
#   scripts/setup-host.sh layer0           Layer-0 only (system + runner + PG roles + auth)
#   scripts/setup-host.sh layer1           Layer-1 only (DbUp: EnsureDatabase + migrations)
#
# Idempotency:
#   - Layer 0 is idempotent (per scripts/bootstrap-laplace-runner.sh).
#   - Layer 1 is idempotent (DbUp's SchemaVersions + IF NOT EXISTS SQL).
#   - The whole script is therefore idempotent: re-running on a fully-
#     configured host is a no-op that prints "already set up."
#
# Requirements:
#   - You CAN run sudo on this host
#   - gh CLI authenticated as a user with admin on SaltyPatron/Laplace
#     (used by Layer 0 to mint runner registration tokens)
#   - PostgreSQL 18 + PostGIS 3.6.3 installed and running
#   - .NET 10 SDK installed
#   - You're standing in the repo root (or pass --repo-dir)

set -euo pipefail

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BOOTSTRAP="$SCRIPT_DIR/bootstrap-laplace-runner.sh"
MIGRATIONS_PROJ="$REPO_DIR/app/Laplace.Migrations/Laplace.Migrations.csproj"
RUNNER_USER="laplace-runner"

MODE="${1:-setup}"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "============================================================"; echo "  $1"; echo "============================================================"; }

usage() {
    cat <<EOF
Usage: $0 <mode>

Modes:
  setup     (default) Full Layer 0 + Layer 1 set-up. Idempotent.
            Calls 'sudo bootstrap-laplace-runner.sh bootstrap' for Layer 0
            (system account, runner, PG roles, peer auth, sudoers), then
            runs 'dotnet run --project Laplace.Migrations -- up' as the
            laplace-runner user for Layer 1 (EnsureDatabase + CREATE EXTENSION
            + role grants).

  status    Print Layer 0 + Layer 1 state. No mutations.

  reset     Tear down Layer 1 (db-nuke) then Layer 0 (bootstrap-laplace-runner.sh
            reset). Requires typing 'RESET' to confirm.

  layer0    Layer-0 only (system + runner + PG roles + auth).
  layer1    Layer-1 only (DbUp: EnsureDatabase + migrations).

After 'setup' completes the host is ready. Trigger CI:
  gh workflow run integration.yml
  # or push to main.
EOF
}

# ---------------------------------------------------------------------------
# Layer-1 runner — invokes the .NET migrations project as laplace-runner
# (whose peer auth resolves to laplace_admin in PG). Requires Layer 0 done.
# ---------------------------------------------------------------------------
ensure_dotnet_present() {
    if ! command -v dotnet >/dev/null 2>&1; then
        red "dotnet not found in PATH. Install .NET 10 SDK first."
        exit 1
    fi
}

# Build artifacts under app/Laplace.Migrations/{bin,obj} MUST be owned by
# laplace-runner — that's the user that 'dotnet run' executes as. If a
# prior `dotnet build` ran under a different user (e.g., `ahart` during
# local dev), the obj/ dir is unwritable to laplace-runner and dotnet's
# NuGet restore step fails with "Access denied" on a *.tmp file.
layer1_clean_foreign_build_artifacts() {
    local cleaned=0
    for dir in obj bin; do
        local d="$REPO_DIR/app/Laplace.Migrations/$dir"
        if [ -e "$d" ] && [ "$(stat -c '%U' "$d")" != "$RUNNER_USER" ]; then
            sudo rm -rf "$d"
            yellow "  - removed $d (was not owned by $RUNNER_USER)"
            cleaned=1
        fi
    done
    return 0
}

# Run a dotnet command as laplace-runner with the right environment.
# Centralises PATH propagation + .NET cache settings + HOME via -H.
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

# ---------------------------------------------------------------------------
# Mode dispatchers
# ---------------------------------------------------------------------------
layer1_build_install_extension() {
    say "Layer 1 — Build + install the laplace PG extension"
    # PGXS build of the C source. Produces extension/laplace.so locally.
    (cd "$REPO_DIR/extension" && make PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config | tail -3)
    # `sudo make install` places .so + .control + .sql into PG's extension
    # dirs. The bounded NOPASSWD sudoers entry for laplace-runner covers
    # this exact invocation pattern (`make install*`), so when this is
    # called from the laplace-runner-owned CI context it's password-free.
    (cd "$REPO_DIR/extension" && sudo make install PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config | tail -3)
    green "✓ laplace.so + .control + .sql installed in PG extension dirs"
}

do_setup() {
    ensure_dotnet_present
    say "Layer 0 — System account, runner, PG roles, peer auth, sudoers, DB, postgis"
    sudo "$BOOTSTRAP" bootstrap
    layer1_build_install_extension
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
  # or just push to main to trigger CI

Reset paths (each independently resettable):
  $0 reset                                   # full teardown
  sudo $BOOTSTRAP reset                      # Layer 0 only
  cd $REPO_DIR && just db-nuke               # Layer 1 only (drop DB)
  cd $REPO_DIR && just db-reset              # Layer 1 (re-apply migrations only)
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
            bash -c "cd '$REPO_DIR/app' && echo 'NUKE' | dotnet run --project '$MIGRATIONS_PROJ' -c Release -- nuke" \
            || yellow "Layer 1 nuke skipped (database absent or unreachable)"
    fi

    say "Layer 0 — bootstrap-laplace-runner.sh reset"
    # Pipe 'RESET' to the bootstrap script so we don't double-prompt.
    echo "RESET" | sudo "$BOOTSTRAP" reset

    green "===== HOST RESET COMPLETE ====="
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
    layer1)
        ensure_dotnet_present
        layer1_build_install_extension
        layer1_up
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
