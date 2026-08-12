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

# THE OPERATOR, resolved ONCE here, where SUDO_USER is still the human.
# Each nested `sudo ... "$BOOTSTRAP"` below runs from an ALREADY-ROOT process, and
# a nested sudo resets SUDO_USER to root — so the bootstrap could never see who
# this host belongs to, and granted its operator permissions to root instead.
# Passed explicitly rather than re-derived on the far side.
LAPLACE_OPERATOR="${SUDO_USER:-$(logname 2>/dev/null || true)}"
export LAPLACE_OPERATOR

MODE="${1:-setup}"

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "============================================================"; echo "  $1"; echo "============================================================"; }

# Warn only AFTER the helpers exist — this block used to call yellow() nine lines
# before it was defined, which under `set -e` aborts the whole run.
if [ -z "$LAPLACE_OPERATOR" ] || [ "$LAPLACE_OPERATOR" = root ]; then
    yellow "WARNING: cannot resolve the operator account (SUDO_USER=${SUDO_USER:-unset})."
    yellow "  Operator-scoped grants (postgres bounce, gh auth mirror) will be SKIPPED"
    yellow "  rather than silently granted to root, which is how this stayed hidden."
    yellow "  Run as: sudo bash scripts/setup-host.sh"
fi

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
    # getent, not /home/$user: root's home is /root, and a hardcoded /home/$user
    # is how bootstrap_runner_gh_auth ended up probing /home/root and skipping.
    local home_src
    home_src="$(getent passwd "${LAPLACE_OPERATOR:-}" 2>/dev/null | cut -d: -f6)/.config/shell/secrets.env"
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
    sudo STRIPE_API_SECRET="$stripe_key" LAPLACE_OPERATOR="$LAPLACE_OPERATOR" "$BOOTSTRAP" stripe || yellow "runner stripe env warned"
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
            # The stat above only inspects the TOP directory. MSBuild writes
            # thousands of files underneath it, and a developer `dotnet build` as
            # ahart — or this script's own Layer 1 extension build, which runs
            # pipeline.sh as root — leaves foreign-owned files inside a directory
            # that still passes the ownership test. layer1_up then runs as
            # $RUNNER_USER and dies on MSB3374 "last access/last write time ...
            # cannot be set" the first time MSBuild touches one of them
            # (measured: 1587 ahart-owned and 121 root-owned files under
            # app/Laplace.*/{obj,bin} against 105 correctly owned).
            # Repair recursively; chown is idempotent and cheap at this size.
            # SHARED, not seized. chown alone made these trees runner-owned at mode
            # 644, so the operator — who IS in the laplace-runner group — got
            # "Access to the path ... is denied" building in their own home
            # directory. Both identities build here: the operator interactively,
            # and $RUNNER_USER via layer1_up and CI. Group-write plus setgid makes
            # the tree writable by both and keeps new files in the shared group
            # instead of re-splitting ownership on the next build.
            if [ -n "$(sudo find "$d" \( ! -user "$RUNNER_USER" -o ! -perm -g+w \) -print -quit 2>/dev/null)" ]; then
                sudo chown -R "$RUNNER_USER:$RUNNER_USER" "$d"
                sudo chmod -R g+w "$d"
                sudo find "$d" -type d -exec chmod g+s {} +
                yellow "  - re-owned + group-shared $d ($RUNNER_USER:$RUNNER_USER, g+w, setgid)"
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
    say "Layer 0.5 — provision external cache + build vendor deps into /opt/laplace"
    # sync-external.sh is gone: it parsed .gitmodules, which 391d9be7 deleted with
    # all 311 submodules when the deps moved to the shared cache. It exited 2 on the
    # missing file, so this layer failed at exactly the point CI did. Provisioning
    # the cache is bootstrap-laplace-runner.sh's job (bootstrap_external_pins),
    # driven by $LAPLACE_EXTERNAL/PINS.tsv.
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
    # This runs pipeline.sh AS ROOT, so every artifact it writes into build/ is
    # root-owned and the operator cannot rebuild afterwards. Measured 2026-08-12:
    # 31 root-owned entries under build/, and cmake died on
    #   file failed to open for writing (Permission denied):
    #     build/engine/core/tests/laplace_core_tests[1]_include.cmake
    # from gtest_discover_tests -- a CMake error that says nothing about
    # ownership. layer1_clean_foreign_build_artifacts already repairs
    # app/*/{obj,bin} for exactly this reason and stops at the managed tree;
    # build/ has the same problem from the same cause and was never covered.
    # Same remedy: shared, not seized -- the operator and $RUNNER_USER both build
    # here, so group-write plus setgid keeps new files in the shared group.
    if [ -d "$REPO_DIR/build" ] \
       && [ -n "$(sudo find "$REPO_DIR/build" \( ! -user "$RUNNER_USER" -o ! -perm -g+w \) -print -quit 2>/dev/null)" ]; then
        sudo chown -R "$RUNNER_USER:$RUNNER_USER" "$REPO_DIR/build"
        sudo chmod -R g+w "$REPO_DIR/build"
        sudo find "$REPO_DIR/build" -type d -exec chmod g+s {} +
        yellow "  - re-owned + group-shared build/ ($RUNNER_USER:$RUNNER_USER, g+w, setgid)"
    fi
    green "✓ extensions installed at ${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
}

do_setup() {
    if [ "$(id -u)" -ne 0 ] && ! sudo -n true 2>/dev/null; then
        red "setup needs root — run: sudo bash scripts/setup-host.sh"
        exit 1
    fi
    ensure_dotnet_present

    say "Layer 0a — account, apt (incl. nginx/stockfish/Qt), /opt/laplace"
    sudo LAPLACE_OPERATOR="$LAPLACE_OPERATOR" "$BOOTSTRAP" prefix

    layer0_5_build_deps

    say "Layer 0b — runner, PG cluster, API unit, chess-lab, secrets seed"
    sudo LAPLACE_OPERATOR="$LAPLACE_OPERATOR" "$BOOTSTRAP" bootstrap

    say "Layer 0c — agent ops lever: guarded runner bounce (sudoers drop-in)"
    # scripts/runner-bounce.sh refuses unless the ingest journal, database
    # backends, CLI processes, and Runner.Worker ALL prove quiet — then and
    # only then restarts the runner unit (the stale-busy wedge measured
    # 2026-08-06 cost ~15min with no lever). This drop-in grants passwordless
    # restart for exactly that one unit and nothing else. Idempotent:
    # content-compared, visudo-validated, removed on validation failure.
    RUNNER_UNIT="actions.runner.SaltyPatron-Laplace.hart-server.service"
    # The rule targets whoever ran setup (SUDO_USER), never a hard-coded name —
    # a hard-coded account grants the lever to the wrong user on another host.
    BOUNCE_USER="${SUDO_USER:-}"
    if [ -z "$BOUNCE_USER" ]; then
        red "SUDO_USER unset — run via sudo so the bounce rule targets the operator account"
        exit 1
    fi
    SUDOERS_LINE="$BOUNCE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart $RUNNER_UNIT"
    SUDOERS_FILE="/etc/sudoers.d/laplace-runner-bounce"
    if [ ! -f "$SUDOERS_FILE" ] || [ "$(sudo cat "$SUDOERS_FILE")" != "$SUDOERS_LINE" ]; then
        printf '%s\n' "$SUDOERS_LINE" | sudo tee "$SUDOERS_FILE" >/dev/null
        sudo chmod 440 "$SUDOERS_FILE"
        if ! sudo visudo -cf "$SUDOERS_FILE"; then
            sudo rm -f "$SUDOERS_FILE"
            red "sudoers drop-in failed validation — removed"
            exit 1
        fi
        green "installed $SUDOERS_FILE"
    else
        green "runner-bounce sudoers already current"
    fi

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
    sudo LAPLACE_OPERATOR="$LAPLACE_OPERATOR" "$BOOTSTRAP" status
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

    echo "RESET" | sudo LAPLACE_OPERATOR="$LAPLACE_OPERATOR" "$BOOTSTRAP" reset
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
