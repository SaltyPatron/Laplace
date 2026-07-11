#!/bin/bash
# =============================================================================
# THE human host entry point for Laplace on Linux.
#
#   sudo bash scripts/setup-host.sh
#
# Machine bring-up only. Runtime Lichess/Stripe credentials are NOT read from
# ~/.config/shell/secrets.env for deploy — CI publish writes /opt/laplace/secrets
# from GitHub repository Secrets (sync from Windows: sync-github-secrets.cmd).
# =============================================================================

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
Usage: sudo bash $0

  setup   (default) Full host bring-up. Idempotent.
  status / reset    Debug / teardown.

After setup: push to main (CI publish owns /opt/laplace/secrets from GitHub
repository Secrets: LICHESS_API, STRIPE_API_SECRET, STRIPE_WEBHOOK_SECRET).
Push secrets from a Windows workstation: scripts\\win\\sync-github-secrets.cmd
EOF
}

ensure_dotnet_present() {
    if ! command -v dotnet >/dev/null 2>&1; then
        red "dotnet not found in PATH. Install .NET 10 SDK first."
        exit 1
    fi
}

# Optional local convenience only — NOT the deploy path.
# Deploy credentials: GitHub Secrets → laplace.yml publish → /opt/laplace/secrets.
seed_billing_from_operator_files() {
    say "Billing secrets — optional local seed (CI publish is authoritative)"
    local home_src="/home/${SUDO_USER:-ahart}/.config/shell/secrets.env"
    local repo_src="$REPO_DIR/.env"
    local src=""
    if [ -f "$home_src" ]; then src="$home_src"
    elif [ -f "$repo_src" ]; then src="$repo_src"
    fi
    if [ -z "$src" ]; then
        yellow "  no local .env — fine; set GitHub secrets then push/publish"
        return 0
    fi

    local stripe_line stripe_key
    stripe_line="$(grep -E '^(STRIPE_API_SECRET|LAPLACE_STRIPE_API_KEY)=' "$src" 2>/dev/null | head -1 || true)"
    if [ -z "$stripe_line" ]; then
        yellow "  no STRIPE_API_SECRET in $src — CI will write stripe.env from GitHub secrets"
        return 0
    fi
    stripe_key="${stripe_line#*=}"

    if [ -x "$STRIPE_BOOTSTRAP" ]; then
        STRIPE_API_SECRET="$stripe_key" \
            "$STRIPE_BOOTSTRAP" --api-key "$stripe_key" --persist-zsh || yellow "stripe-dev.env bootstrap warned"
    fi
    sudo STRIPE_API_SECRET="$stripe_key" "$BOOTSTRAP" stripe || yellow "runner stripe env warned"
    green "✓ local billing seed from $src (overwritten by next CI publish)"
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
        || yellow "(database not yet present; run setup)"
}

layer0_5_build_deps() {
    say "Layer 0.5 — sync external + build vendor deps into /opt/laplace"
    if [ -x "$REPO_DIR/scripts/sync-external.sh" ]; then
        bash "$REPO_DIR/scripts/sync-external.sh" || yellow "sync-external warned — continuing"
    fi
    bash "$REPO_DIR/scripts/build-system-deps.sh"
}

layer1_build_install_extensions() {
    say "Layer 1 — Build + install extensions (pipeline.sh; same path as CI)"
    local setvars=/opt/intel/oneapi/setvars.sh
    if [ ! -f "$setvars" ]; then
        red "missing $setvars — install Intel oneAPI before setup-host Layer 1"
        return 1
    fi
    set +u
    # shellcheck disable=SC1090
    source "$setvars" --force >/dev/null
    set -u
    if [ -z "${MKLROOT:-}" ] || [ -z "${TBBROOT:-}" ] || [ -z "${CMPLR_ROOT:-}" ]; then
        red "oneAPI setvars did not export MKLROOT/TBBROOT/CMPLR_ROOT"
        return 1
    fi
    (
        cd "$REPO_DIR"
        bash scripts/pipeline.sh build
        bash scripts/pipeline.sh install
    )
    green "✓ extensions installed at ${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
}

do_setup() {
    if [ "$(id -u)" -ne 0 ] && ! sudo -n true 2>/dev/null; then
        red "setup needs root — run: sudo bash scripts/setup-host.sh"
        exit 1
    fi
    ensure_dotnet_present

    say "Layer 0a — account, apt (incl. nginx/stockfish/Qt), /opt/laplace"
    sudo "$BOOTSTRAP" prefix

    layer0_5_build_deps

    say "Layer 0b — runner, PG cluster, API unit, chess-lab, secrets seed"
    sudo "$BOOTSTRAP" bootstrap

    # Billing is part of setup — not a separate human mode.
    seed_billing_from_operator_files

    layer1_build_install_extensions
    layer1_up

    say "DONE — host ready. CI owns deploys + runtime secrets."
    cat <<EOF

  Human once:  sudo bash scripts/setup-host.sh
  Secrets:     scripts\\win\\sync-github-secrets.cmd  (from Windows .env → GitHub Secrets)
  CI forever:  push to main → laplace.yml publish → /opt/laplace/secrets

EOF
}

do_status() {
    say "Layer 0 — bootstrap status"
    sudo "$BOOTSTRAP" status
    if id -u "$RUNNER_USER" >/dev/null 2>&1; then
        ensure_dotnet_present
        layer1_status
    else
        yellow "(laplace-runner not present yet)"
    fi
}

do_reset() {
    cat <<EOF
========================================================================
RESET — Tear down EVERYTHING this host provides for Laplace
========================================================================
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
            || yellow "Layer 1 nuke skipped"
    fi

    echo "RESET" | sudo "$BOOTSTRAP" reset
    green "===== HOST RESET COMPLETE ====="
}

# Kept as alias — setup already seeds billing. Useful to re-seed after editing secrets.env.
do_stripe() {
    seed_billing_from_operator_files
    green "✓ Stripe/Lichess secret re-seed complete"
}

case "$MODE" in
    setup)          do_setup ;;
    status)         do_status ;;
    reset)          do_reset ;;
    stripe)         do_stripe ;;
    -h|--help|help) usage ;;
    *)
        red "Unknown mode: $MODE — use setup/status/reset"
        usage
        exit 64
        ;;
esac
