#!/bin/bash
# scripts/bootstrap-laplace-runner.sh
#
# Layer 0 — one-time root setup (idempotent + reversible) for the Laplace
# CI runner identity. Per ADR 0018 (three-layer architecture), this script
# does ONLY what requires root and only what is independent of substrate
# schema. Database creation, extension install, schema, and seed data are
# Layer 1 concerns owned by `app/Laplace.Migrations/` (DbUp) and `just`
# recipes.
#
# What this manages (idempotent):
#   1. System account `laplace-runner` (no home in /home, no shell) + interactive
#      dev user added to that group so `just *` recipes write to laplace-runner-
#      owned files (obj/, bin/, /opt/laplace) via group perms — no sudo
#   2. GitHub Actions runner installed at /var/lib/agents/laplace-runner/actions-runner
#      (on the dedicated vg-hosting/lv-agents LV — older bootstraps placed it at
#      /var/lib/laplace-runner on /var; bootstrap_migrate_runner_home performs
#      the one-time move).
#   3. systemd service running as laplace-runner
#   4. PG roles laplace_admin / laplace_app / laplace_readonly
#   5. pg_ident.conf  → maps OS user laplace-runner (and ahart, for interactive
#      dev) onto PG role laplace_admin
#   6. pg_hba.conf    → peer auth for laplace_admin via that map
#   7. /etc/ld.so.conf.d/laplace.conf → /opt/laplace/lib + the Intel oneAPI
#      runtime (libsvml/libirc/libimf) registered with the dynamic linker
#      so engine .so files (and the PG extensions that DT_NEED them) load
#      under the `postgres` user without LD_LIBRARY_PATH
#   8. Sweep of stale Laplace + Hartonomous installs from /usr/local/lib +
#      /usr/lib/postgresql/$PG_VERSION/lib + /usr/share/postgresql/$PG_VERSION/extension
#      (left behind by prior `sudo cmake --install` runs that used the
#      old install layout; superseded by the /opt/laplace-staged layout)
#   9. postgresql.conf extension_control_path + dynamic_library_path →
#      point the running PG at /opt/laplace/{share,lib}/postgresql/$PG_VERSION
#      (so `just install-laplace-prefix` lands extensions where CREATE
#      EXTENSION finds them, without sudo on per-iteration); managed
#      between marker comments so re-runs replace cleanly instead of
#      appending duplicates
#  10. Removal of legacy /etc/sudoers.d/laplace-runner (the prior bounded-
#      NOPASSWD-for-cmake-install workaround). With CMAKE_INSTALL_PREFIX
#      = /opt/laplace (laplace-runner-group writable, setgid) plus PG's
#      extension_control_path / dynamic_library_path pointing there,
#      the install is sudo-free per ADR 0019 amendment 2026-05-23.
#  11. Cleanup of legacy state (ahart-as-superuser, ahart DB, old runner
#      under /home/ahart/actions-runner)
#  12. /opt/laplace/external/ — empty cache directory with the right perms
#      (ahart:laplace-runner setgid 2775). POPULATION is project state, not
#      machine state — it belongs in the pipeline. scripts/sync-external.sh
#      (invoked from integration.yml or run by a developer via group membership)
#      maintains it as non-bare checkouts at .gitmodules-pinned SHAs, idempotent.
#  13. /var/lib/laplace-runner/.config/gh/hosts.yml — gh CLI auth for the
#      runner user, derived from $SUDO_USER's existing gh config. Lets the
#      runner do `gh issue edit` / `gh api` per CLAUDE.md cadence without
#      manual login (the runner has no shell — no interactive `gh auth login`
#      is possible).
#
# What this does NOT touch (those live in Layer 1 / Justfile):
#   - The `laplace` database (DbUp creates it via EnsureDatabase)
#   - The `postgis` / `laplace` extensions (DbUp's migrations do CREATE EXTENSION)
#   - Any substrate schema (the `laplace` extension's .sql files own that)
#   - Seed data (just seed-t0)
#
# Usage:
#   sudo scripts/bootstrap-laplace-runner.sh bootstrap   (default — idempotent set-up)
#   sudo scripts/bootstrap-laplace-runner.sh status      (show current state)
#   sudo scripts/bootstrap-laplace-runner.sh reset       (full tear-down)
#
# Requires for `bootstrap`:
#   - gh CLI authenticated for $SUDO_USER as an admin on SaltyPatron/Laplace
#     (script uses $SUDO_USER's gh config to mint runner tokens)

set -eo pipefail

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
RUNNER_USER="laplace-runner"
RUNNER_GROUP="laplace-runner"
# Runner home lives on the dedicated vg-hosting/lv-agents LV (64G), not /var.
# Older bootstraps placed it at /var/lib/laplace-runner; bootstrap_migrate_runner_home
# below handles the one-time move from the legacy location.
RUNNER_HOME="/var/lib/agents/laplace-runner"
RUNNER_HOME_LEGACY="/var/lib/laplace-runner"
RUNNER_DIR="$RUNNER_HOME/actions-runner"
PG_VERSION="18"
# Substrate cluster lives at /opt/laplace/pgsql-18/data (the custom-built
# PG 18.3 produced by `just build-deps`, against /opt/laplace GEOS 3.12.2
# + PROJ 9.x). System apt-installed postgresql@18-main on /etc/postgresql/18/main
# is stopped + disabled — the substrate must run against the consistent
# /opt/laplace dep set so liblwgeom's libgeos/libproj transitive references
# all resolve to one version per ABI. (Two libproj versions in the backend
# segfault on shutdown: libproj.so.22's UnitOfMeasure destructor double-frees
# state libproj.so.25 already freed. Verified via core dump 2026-05-24.)
LAPLACE_PG_PREFIX="/opt/laplace/pgsql-18"
LAPLACE_PG_DATA="$LAPLACE_PG_PREFIX/data"
LAPLACE_PG_PORT="5432"
LAPLACE_PG_SOCKET_DIR="/var/run/postgresql"
LAPLACE_PG_SERVICE="laplace-postgresql.service"
# Legacy paths still referenced for the system-PG teardown step; once
# bootstrap_disable_system_postgresql runs, the substrate stops touching
# /etc/postgresql/$PG_VERSION/main entirely.
PG_CONFIG_DIR="/etc/postgresql/$PG_VERSION/main"
REPO="SaltyPatron/Laplace"
REPO_URL="https://github.com/$REPO"
RUNNER_VERSION="v2.334.0"
RUNNER_TARBALL="actions-runner-linux-x64-${RUNNER_VERSION#v}.tar.gz"
RUNNER_DL_URL="https://github.com/actions/runner/releases/download/$RUNNER_VERSION/$RUNNER_TARBALL"
SUDOERS_FILE="/etc/sudoers.d/laplace-runner"
# Auth configs live OUTSIDE the data dir so developers in the laplace-runner
# group can read + edit them without sudo. PG enforces 0700 on the data dir
# itself (hard startup check), so files inside it are unreachable to anyone
# but the postgres process. PG supports hba_file + ident_file pointing
# anywhere — we put them at /opt/laplace/pgsql-18/conf/ which is 2750
# (group laplace-runner, group readable + writable via setgid + group bit).
LAPLACE_PG_CONF_DIR="$LAPLACE_PG_PREFIX/conf"
PG_HBA_FILE="$LAPLACE_PG_CONF_DIR/pg_hba.conf"
PG_IDENT_FILE="$LAPLACE_PG_CONF_DIR/pg_ident.conf"
PG_POSTGRESQL_CONF="$LAPLACE_PG_DATA/postgresql.conf"
RUNNER_SERVICE="actions.runner.SaltyPatron-Laplace.hart-server.service"

GH_SUDO_USER="${SUDO_USER:-ahart}"
MODE="${1:-bootstrap}"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "=== $1 ==="; }

require_root() {
    if [ "$EUID" -ne 0 ]; then
        red "Must run as root. Try: sudo $0 $MODE"
        exit 1
    fi
}

usage() {
    cat <<EOF
Usage: $0 <mode>

Modes:
  bootstrap   Idempotent set-up (default if no mode given)
              - Creates system account, runner, PG roles, peer auth, sudoers
              - Safe to re-run; only adds what's missing
  status      Print current state (no changes)
  reset       Tear down everything this script created
              - Deregisters + removes runner
              - Drops PG roles (and DBs they own)
              - Removes pg_hba/pg_ident entries, sudoers file
              - Removes the laplace-runner system account
              - REQUIRES typing 'RESET' to confirm

After bootstrap, Layer 1 (DbUp) creates the database + extensions:
  just db-up   (or: dotnet run --project app/Laplace.Migrations -- up)
EOF
}

mint_gh_token() {
    # Mint a registration or remove token via gh CLI run as the invoking user.
    # \$1 = "registration-token" or "remove-token"
    local kind="$1"
    sudo -u "$GH_SUDO_USER" -H gh api -X POST \
        "repos/$REPO/actions/runners/$kind" --jq '.token' 2>/dev/null || true
}

# ---------------------------------------------------------------------------
# bootstrap_*  — each step is its own function so order is obvious + reset
#                can call the inverse in reverse order
# ---------------------------------------------------------------------------

bootstrap_user() {
    say "Ensure system account: $RUNNER_USER"
    if id -u "$RUNNER_USER" >/dev/null 2>&1; then
        green "✓ User $RUNNER_USER exists (UID $(id -u "$RUNNER_USER"))"
    else
        useradd --system \
            --no-create-home \
            --home-dir "$RUNNER_HOME" \
            --shell /usr/sbin/nologin \
            --user-group \
            "$RUNNER_USER"
        green "✓ Created system user $RUNNER_USER (UID $(id -u "$RUNNER_USER"))"
    fi
    mkdir -p "$RUNNER_HOME"
    chown -R "$RUNNER_USER:$RUNNER_GROUP" "$RUNNER_HOME"
    chmod 750 "$RUNNER_HOME"
    green "✓ $RUNNER_HOME owned by $RUNNER_USER:$RUNNER_GROUP (mode 750)"

    # Add the interactive dev user (GH_SUDO_USER — typically `ahart`) to
    # the laplace-runner group. /opt/laplace is laplace-runner:laplace-runner
    # mode 2775 setgid. Group membership gives the dev user read+write+execute
    # for almost all iteration: just db-up, just test-app, sync-external.sh,
    # cmake --build, running binaries from build/, psql against the deployed
    # extensions, etc.
    #
    # EXCEPTION: `cmake --install` calls chmod, which requires being the file
    # owner (not just group write — Linux chmod is unconditionally owner-only
    # by POSIX). Local installs as the dev user must use `sudo -u laplace-runner
    # cmake --install build`. CI runs as laplace-runner natively so its installs
    # work without any sudo.
    #
    # Idempotent — adding to a group you're already in is a no-op.
    if id "$GH_SUDO_USER" >/dev/null 2>&1; then
        if id -nG "$GH_SUDO_USER" | tr ' ' '\n' | grep -qx "$RUNNER_GROUP"; then
            green "✓ $GH_SUDO_USER already in $RUNNER_GROUP group"
        else
            usermod -aG "$RUNNER_GROUP" "$GH_SUDO_USER"
            yellow "✓ Added $GH_SUDO_USER to $RUNNER_GROUP group"
            yellow "  → existing shells need 'newgrp $RUNNER_GROUP' (or a new terminal)"
            yellow "    to pick up the new group membership; new shells inherit it automatically"
        fi
    fi
}

bootstrap_build_environment() {
    # System packages + /opt/laplace prefix for the custom-built dependency
    # chain (Epic B: PostgreSQL + PostGIS + PROJ + GEOS + GDAL + Eigen +
    # Spectra + BLAKE3 as git submodules, built with Intel icx/icpx per
    # ADRs 0028 + 0032).
    #
    # Idempotent — apt install is a no-op when packages are present;
    # mkdir -p + chown are safe to re-run.

    say "Build environment: apt build-deps + /opt/laplace prefix"

    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        build-essential cmake ninja-build autoconf automake libtool pkg-config \
        bison flex perl \
        sqlite3 \
        libssl-dev zlib1g-dev libreadline-dev uuid-dev \
        libxml2-dev libjson-c-dev libicu-dev \
        libsqlite3-dev libtiff-dev libcurl4-openssl-dev \
        libpcre2-dev libgeotiff-dev libpng-dev libwebp-dev \
        libjpeg-turbo8-dev libnetcdf-dev libhdf5-dev libexpat1-dev \
        >/dev/null
    green "✓ Build-deps apt packages present"
    # sqlite3 (binary, not just libsqlite3-dev) is required by PROJ's CMake
    # to populate proj.db during configure (PROJ 6+ replaced datumgrid files

    # Custom-build install prefix. Owned by laplace-runner (the CI runner's
    # identity) with laplace-runner as the group, mode 2775 (setgid + g+w):
    #   - CI's `cmake --install` runs as laplace-runner → it owns the install
    #     destinations → `chmod` calls in install rules succeed (chmod is
    #     owner-only; group write isn't enough).
    #   - Developers (ahart) in the laplace-runner group can READ + WRITE
    #     files in /opt/laplace via group permissions (setgid keeps new
    #     files' group as laplace-runner regardless of creator). They
    #     CANNOT chmod laplace-runner-owned files. For local installs
    #     (cmake --install), developers must `sudo -u laplace-runner cmake
    #     --install build` OR use a per-user CMAKE_INSTALL_PREFIX.
    # The earlier shape (chown to $GH_SUDO_USER) caused the chmod-by-non-owner
    # collision when CI installed over ahart-owned files; flipped to
    # laplace-runner ownership per the 2026-05-24 alignment.
    mkdir -p /opt/laplace
    # /opt/laplace must be laplace-runner-owned with setgid 2775 (group
    # laplace-runner inherits to new entries) and NO sticky bit, so that
    # any group member can unlink files regardless of owner. That property —
    # plus the pre-wipe install(CODE) block at the top of the project's
    # CMakeLists.txt — makes `cmake --install build` work for both ahart
    # (local) and laplace-runner (CI) without sudo and without ownership
    # coordination. The pre-wipe deletes existing files; cmake then creates
    # fresh files owned by the installer; the post-copy chmod() succeeds
    # because the installer owns its own newly-created files.
    chown "$RUNNER_USER:$RUNNER_GROUP" /opt/laplace
    chmod 2775 /opt/laplace
    green "✓ /opt/laplace: $RUNNER_USER:$RUNNER_GROUP mode 2775 (setgid, no sticky)"
}

bootstrap_migrate_runner_home() {
    # The runner was originally placed at /var/lib/laplace-runner — which is
    # on the /var LV (cramped). vg-hosting/lv-agents is mounted at
    # /var/lib/agents (64G dedicated, mostly empty) and was set up precisely
    # for this kind of agent installation. Move the runner home to its
    # rightful drive. Idempotent — no-op once migrated.
    say "Migrate runner home: $RUNNER_HOME_LEGACY -> $RUNNER_HOME (dedicated lv-agents LV)"

    if [ ! -d "$RUNNER_HOME_LEGACY" ] || [ -z "$(ls -A "$RUNNER_HOME_LEGACY" 2>/dev/null)" ]; then
        green "✓ No legacy $RUNNER_HOME_LEGACY content to migrate"
        # Still ensure the new home exists, owned correctly.
        mkdir -p "$RUNNER_HOME"
        chown -R "$RUNNER_USER:$RUNNER_GROUP" "$RUNNER_HOME"
        chmod 750 "$RUNNER_HOME"
        return
    fi

    if [ ! -d /var/lib/agents ] || ! mountpoint -q /var/lib/agents; then
        yellow "  /var/lib/agents not mounted — skipping migration (keeping runner at $RUNNER_HOME_LEGACY)"
        # Adjust globals so the rest of the bootstrap uses the legacy path.
        RUNNER_HOME="$RUNNER_HOME_LEGACY"
        RUNNER_DIR="$RUNNER_HOME/actions-runner"
        return
    fi

    # Stop the runner service while we move things.
    local runner_was_active=0
    if systemctl is-active --quiet "$RUNNER_SERVICE" 2>/dev/null; then
        runner_was_active=1
        systemctl stop "$RUNNER_SERVICE"
        echo "  stopped $RUNNER_SERVICE for migration"
    fi

    # Rsync everything (preserves hardlinks, xattrs, ownership).
    mkdir -p "$RUNNER_HOME"
    rsync -aHAX "$RUNNER_HOME_LEGACY/" "$RUNNER_HOME/"
    chown -R "$RUNNER_USER:$RUNNER_GROUP" "$RUNNER_HOME"
    chmod 750 "$RUNNER_HOME"
    green "✓ Copied $RUNNER_HOME_LEGACY -> $RUNNER_HOME"

    # Update the OS user's home-dir metadata to the new location.
    if [ "$(getent passwd "$RUNNER_USER" | cut -d: -f6)" != "$RUNNER_HOME" ]; then
        usermod -d "$RUNNER_HOME" "$RUNNER_USER"
        green "✓ usermod -d $RUNNER_HOME $RUNNER_USER"
    fi

    # The systemd unit references the OLD path via WorkingDirectory + ExecStart.
    # The runner's svc.sh writes these into /etc/systemd/system/<unit>; rewrite
    # the paths in place (between the unit's [Service] lines).
    local unit_file="/etc/systemd/system/$RUNNER_SERVICE"
    if [ -f "$unit_file" ] && grep -q "$RUNNER_HOME_LEGACY" "$unit_file"; then
        sed -i "s|$RUNNER_HOME_LEGACY|$RUNNER_HOME|g" "$unit_file"
        systemctl daemon-reload
        green "✓ Rewrote $unit_file: $RUNNER_HOME_LEGACY -> $RUNNER_HOME"
    fi

    # Archive the legacy tree (don't blow it away — keeps a fallback for a
    # single bootstrap iteration in case the new location has any issue).
    local archive="${RUNNER_HOME_LEGACY}.pre-migration-$(date +%Y%m%d-%H%M%S)"
    mv "$RUNNER_HOME_LEGACY" "$archive"
    green "✓ Archived legacy tree to $archive (rm -rf when satisfied)"

    if [ "$runner_was_active" -eq 1 ]; then
        systemctl start "$RUNNER_SERVICE"
        sleep 1
        if systemctl is-active --quiet "$RUNNER_SERVICE"; then
            green "✓ Runner service restarted from new location"
        else
            red "✗ Runner service failed to start from new location"
            yellow "  Check: journalctl -u $RUNNER_SERVICE -n 30"
            yellow "  Rollback: sudo mv $archive $RUNNER_HOME_LEGACY"
            yellow "           sudo sed -i \"s|$RUNNER_HOME|$RUNNER_HOME_LEGACY|g\" $unit_file"
            yellow "           sudo usermod -d $RUNNER_HOME_LEGACY $RUNNER_USER"
            yellow "           sudo systemctl daemon-reload && sudo systemctl start $RUNNER_SERVICE"
        fi
    fi
}

bootstrap_legacy_runner_teardown() {
    say "Tear down legacy runner under /home/ahart/actions-runner (if any)"
    local old="/home/ahart/actions-runner"
    if [ ! -d "$old" ]; then
        green "✓ No legacy runner under /home/ahart — nothing to tear down"
        return
    fi
    yellow "Found legacy runner; tearing down"

    local svc
    svc=$(systemctl list-unit-files --type=service 2>/dev/null \
          | grep -oE 'actions\.runner\.[^[:space:]]+\.service' | head -1 || true)
    if [ -n "$svc" ]; then
        systemctl stop "$svc" 2>/dev/null || true
        systemctl disable "$svc" 2>/dev/null || true
        echo "  stopped + disabled $svc"
    fi

    if [ -x "$old/svc.sh" ]; then
        (cd "$old" && ./svc.sh uninstall 2>/dev/null) || true
    fi

    local token
    token=$(mint_gh_token "remove-token")
    if [ -n "$token" ] && [ -x "$old/config.sh" ]; then
        (cd "$old" && sudo -u "$GH_SUDO_USER" -H ./config.sh remove --token "$token" 2>/dev/null) || true
        green "✓ Deregistered legacy runner from GitHub"
    fi

    local archive="/tmp/laplace-runner-prev-$(date +%s)"
    mv "$old" "$archive"
    rm -rf /home/ahart/_work 2>/dev/null || true
    green "✓ Legacy runner archived at $archive"
}

bootstrap_runner_install() {
    say "Install actions-runner at $RUNNER_DIR"
    mkdir -p "$RUNNER_DIR"
    chown "$RUNNER_USER:$RUNNER_GROUP" "$RUNNER_DIR"

    if [ -f "$RUNNER_DIR/config.sh" ]; then
        green "✓ Runner already extracted at $RUNNER_DIR"
        return
    fi

    local tarball="/tmp/$RUNNER_TARBALL"
    if [ ! -f "$tarball" ]; then
        echo "Downloading $RUNNER_DL_URL ..."
        curl -sSLo "$tarball" "$RUNNER_DL_URL"
        chown "$RUNNER_USER:$RUNNER_GROUP" "$tarball"
    fi
    sudo -u "$RUNNER_USER" -H tar xzf "$tarball" -C "$RUNNER_DIR"
    rm -f "$tarball"
    green "✓ Runner extracted"
}

bootstrap_runner_register() {
    say "Register runner with $REPO_URL"

    # Idempotency check — the actions-runner writes a `.runner` file (its
    # config + credentials) when config.sh succeeds. If that file exists,
    # this host is already registered with GitHub and re-running config.sh
    # would fail with "Cannot configure: already configured". Use the
    # presence of .runner as the canonical idempotency signal, plus the
    # systemd unit file as a second guard.
    if [ -f "$RUNNER_DIR/.runner" ] \
       || [ -f "/etc/systemd/system/$RUNNER_SERVICE" ]; then
        green "✓ Runner already registered (.runner or service unit present) — skipping config.sh"
        return
    fi

    local token
    token=$(mint_gh_token "registration-token")
    if [ -z "$token" ]; then
        red "Could not mint registration token via gh CLI as $GH_SUDO_USER"
        red "Ensure: sudo -u $GH_SUDO_USER gh auth status   works with admin scope on $REPO"
        exit 1
    fi

    (cd "$RUNNER_DIR" && sudo -u "$RUNNER_USER" -H ./config.sh \
        --url "$REPO_URL" \
        --token "$token" \
        --name hart-server \
        --labels laplace,oneapi,postgres-18,dotnet-10,avx2 \
        --work _work \
        --unattended \
        --replace)
    green "✓ Registered runner as 'hart-server'"
}

bootstrap_runner_service() {
    say "Install + start systemd service ($RUNNER_USER)"
    local unit_file="/etc/systemd/system/$RUNNER_SERVICE"
    if [ -f "$unit_file" ]; then
        green "✓ Service unit $unit_file already exists — skipping svc.sh install"
        # Ensure it's enabled + running.
        systemctl enable "$RUNNER_SERVICE" >/dev/null 2>&1 || true
        if systemctl is-active "$RUNNER_SERVICE" >/dev/null 2>&1; then
            green "✓ Service already active"
        else
            systemctl start "$RUNNER_SERVICE"
            sleep 1
            green "✓ Service started"
        fi
    else
        (cd "$RUNNER_DIR" && ./svc.sh install "$RUNNER_USER" && ./svc.sh start)
        sleep 1
        green "✓ Service installed + started"
    fi
    (cd "$RUNNER_DIR" && ./svc.sh status | head -3) || true
}

# ---------------------------------------------------------------------------
# bootstrap_disable_system_postgresql — stop + disable apt-installed
# postgresql@$PG_VERSION-main so the substrate doesn't share /var/run/postgresql
# with another cluster. Required because the substrate cluster (laplace-postgresql)
# binds /var/run/postgresql/.s.PGSQL.$LAPLACE_PG_PORT, which the system unit
# already owns by default. Idempotent — silent no-op if the system unit is
# already disabled or not installed.
# ---------------------------------------------------------------------------
bootstrap_disable_system_postgresql() {
    say "Disable system postgresql@$PG_VERSION-main (substrate uses /opt/laplace cluster only)"
    local sys_unit="postgresql@${PG_VERSION}-main.service"
    local sys_wrapper="postgresql.service"

    # systemctl list-unit-files only shows unit TEMPLATES (postgresql@.service),
    # not INSTANCES (postgresql@18-main.service). Probe the instance via
    # status — exit 0/3 means "known to systemd"; exit 4 means "not loaded."
    # `|| sys_status=$?` keeps set -e from killing us when status returns 3.
    local sys_status=0
    systemctl status "$sys_unit" --no-pager >/dev/null 2>&1 || sys_status=$?
    if [ $sys_status -eq 4 ]; then
        green "✓ System $sys_unit not present — nothing to disable"
        return
    fi

    # Stop forcefully. system PG owns /var/run/postgresql/.s.PGSQL.5432 by
    # default, and the substrate cluster (same port) collides + fails if
    # the system unit isn't down BEFORE we start ours.
    if systemctl is-active --quiet "$sys_unit"; then
        systemctl stop "$sys_unit"
        green "✓ Stopped $sys_unit"
    else
        green "✓ $sys_unit already stopped"
    fi

    if systemctl is-enabled --quiet "$sys_unit" 2>/dev/null; then
        systemctl disable "$sys_unit" 2>/dev/null || true
        green "✓ Disabled $sys_unit"
    else
        green "✓ $sys_unit already disabled (or not enableable)"
    fi

    # The postgresql.service wrapper auto-starts all postgresql@*-main units
    # on boot. Mask it so socket activation + boot-time start can't bring
    # system PG back behind our cluster's back.
    if systemctl is-enabled --quiet "$sys_wrapper" 2>/dev/null; then
        systemctl mask "$sys_wrapper" >/dev/null 2>&1 || true
        green "✓ Masked $sys_wrapper wrapper (prevents boot-time auto-start)"
    fi
}

# ---------------------------------------------------------------------------
# bootstrap_laplace_pg_cluster — provision + start the /opt/laplace/pgsql-18
# substrate cluster as a systemd unit running as laplace-runner.
#
# Lays down (idempotent at every step):
#   1. initdb $LAPLACE_PG_DATA as laplace-runner if data dir not yet a cluster
#   2. postgresql.conf: port 5432, listen_addresses=localhost,
#      unix_socket_directories=/var/run/postgresql,/tmp,
#      extension_control_path + dynamic_library_path → /opt/laplace,
#      logging to /opt/laplace/pgsql-18/log/
#   3. pg_hba.conf: peer auth via laplace_map for local connections
#   4. pg_ident.conf: laplace_map maps OS users (laplace-runner, ahart) → laplace_admin
#   5. systemd unit /etc/systemd/system/laplace-postgresql.service
#   6. /var/run/postgresql ownership so laplace-runner can bind the socket
#   7. systemctl daemon-reload + enable + start + wait for ready
#
# The cluster owns the /var/run/postgresql socket at port 5432 — same shape
# as the system cluster it replaces, so existing `psql -d laplace -U laplace_admin`
# invocations (DbUp, Justfile, pg_regress) Just Work without per-command
# PGHOST/PGPORT overrides.
# ---------------------------------------------------------------------------
bootstrap_laplace_pg_cluster() {
    say "Provision /opt/laplace/pgsql-18 cluster (substrate runtime PG)"

    if [ ! -x "$LAPLACE_PG_PREFIX/bin/postgres" ]; then
        red "✗ $LAPLACE_PG_PREFIX/bin/postgres missing — run \`just build-deps\` first"
        return 1
    fi

    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$LAPLACE_PG_PREFIX/log"
    # 2775 conf dir: setgid + group laplace-runner + group writable.
    # Developers in the laplace-runner group (ahart) can read + edit
    # pg_hba.conf / pg_ident.conf without sudo.
    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$LAPLACE_PG_CONF_DIR"

    # initdb if not yet a cluster. Detect by PG_VERSION marker file.
    if [ ! -f "$LAPLACE_PG_DATA/PG_VERSION" ]; then
        if [ -d "$LAPLACE_PG_DATA" ] && [ -n "$(ls -A "$LAPLACE_PG_DATA" 2>/dev/null)" ]; then
            yellow "  $LAPLACE_PG_DATA exists + non-empty but no PG_VERSION — bailing rather than wipe"
            yellow "  Manual fix: rm -rf $LAPLACE_PG_DATA && sudo $0 bootstrap"
            return 1
        fi
        install -d -m 0700 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$LAPLACE_PG_DATA"
        sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/initdb" \
            -D "$LAPLACE_PG_DATA" \
            --auth-host=trust --auth-local=peer \
            --username=laplace_admin \
            --no-locale --encoding=UTF8 \
            >/dev/null
        green "✓ initdb'd $LAPLACE_PG_DATA"
    else
        green "✓ Cluster already initialized at $LAPLACE_PG_DATA"
    fi

    # Ensure the data dir is fully owned by $RUNNER_USER. PG refuses to
    # start when the data dir contents have mixed ownership (a prior
    # interactive `pg_ctl start` as a different user can leave files
    # behind owned by that user). Chown is idempotent on a correctly-
    # owned tree.
    chown -R "$RUNNER_USER:$RUNNER_GROUP" "$LAPLACE_PG_DATA"
    chmod 0700 "$LAPLACE_PG_DATA"
    green "✓ $LAPLACE_PG_DATA owned by $RUNNER_USER (mode 0700)"

    # Configure postgresql.conf. Idempotent — strip prior managed block,
    # then append fresh values. Same begin/end marker pattern as
    # bootstrap_pg_extension_paths.
    local marker_begin="# >>> laplace-runner managed: cluster config >>>"
    local marker_end="# <<< laplace-runner managed: cluster config <<<"
    sudo -u "$RUNNER_USER" sed -i \
        -e "/$marker_begin/,/$marker_end/d" \
        -e '/^[[:space:]]*#\?[[:space:]]*port[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*listen_addresses[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*unix_socket_directories[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*extension_control_path[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*dynamic_library_path[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*logging_collector[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*log_directory[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*log_filename[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*log_file_mode[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*hba_file[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*ident_file[[:space:]]*=/d' \
        "$PG_POSTGRESQL_CONF"
    sudo -u "$RUNNER_USER" tee -a "$PG_POSTGRESQL_CONF" >/dev/null <<EOF

$marker_begin
# Substrate cluster — written by scripts/bootstrap-laplace-runner.sh.
# Owns /var/run/postgresql/.s.PGSQL.$LAPLACE_PG_PORT so existing
# psql -d laplace -U laplace_admin invocations connect here.
port = $LAPLACE_PG_PORT
listen_addresses = 'localhost'
unix_socket_directories = '$LAPLACE_PG_SOCKET_DIR,/tmp'
# Extension paths — same shape as the prior system-PG bootstrap.
# PG 18 appends '/extension' to each custom extension_control_path entry,
# so this is the SHAREDIR, not the extension subdir.
extension_control_path = '/opt/laplace/share/postgresql/$PG_VERSION:\$system'
dynamic_library_path = '\$libdir:/opt/laplace/lib/postgresql/$PG_VERSION'
# Logging — collect to /opt/laplace/pgsql-18/log/ so logs survive the
# systemd unit's stdout/stderr handling. log_file_mode 0640 + dir owned
# by laplace-runner with group laplace-runner makes logs group-readable;
# any dev in the laplace-runner group (ahart) tails without sudo.
logging_collector = on
log_directory = '$LAPLACE_PG_PREFIX/log'
log_filename = 'postgresql-%Y-%m-%d_%H%M%S.log'
log_file_mode = 0640
# hba_file + ident_file outside the 0700 data dir so the
# laplace-runner group can read+edit them without sudo.
hba_file = '$PG_HBA_FILE'
ident_file = '$PG_IDENT_FILE'
$marker_end
EOF
    green "✓ Wrote substrate cluster config to $PG_POSTGRESQL_CONF"

    # pg_hba.conf — overwrite with our managed block ONLY. PG processes
    # rules top-to-bottom and the FIRST match wins, so any initdb default
    # `local all all peer` rule above ours would be matched before the
    # mapped `local all laplace_admin peer map=laplace_map` rule and
    # peer auth would fail (OS user laplace-runner != PG role laplace_admin
    # without the map). Easier than ordering preservation: own the file
    # entirely. This cluster is the substrate's, not a generic PG instance.
    sudo -u "$RUNNER_USER" tee "$PG_HBA_FILE" >/dev/null <<EOF
# Substrate cluster auth — managed by scripts/bootstrap-laplace-runner.sh.
# Do not hand-edit; re-run \`sudo just bootstrap\` to regenerate.
#
# laplace_map = OS users (laplace-runner, ahart, postgres) → PG role
# laplace_admin. Mapping defined in pg_ident.conf.
local   all             laplace_admin                           peer map=laplace_map
local   all             all                                     peer
host    all             all             127.0.0.1/32            trust
host    all             all             ::1/128                 trust
EOF
    sudo -u "$RUNNER_USER" chmod 0600 "$PG_HBA_FILE"
    green "✓ Wrote substrate cluster pg_hba.conf (managed block as full file)"

    # pg_ident.conf — laplace_map.
    if ! sudo -u "$RUNNER_USER" grep -q "^laplace_map" "$PG_IDENT_FILE" 2>/dev/null; then
        sudo -u "$RUNNER_USER" tee -a "$PG_IDENT_FILE" >/dev/null <<EOF

# laplace_map — OS users authorized to connect as laplace_admin via peer auth.
laplace_map   laplace-runner   laplace_admin
laplace_map   ahart            laplace_admin
laplace_map   postgres         laplace_admin
EOF
        green "✓ Wrote laplace_map to $PG_IDENT_FILE"
    else
        green "✓ laplace_map already present in $PG_IDENT_FILE"
    fi

    # /var/run/postgresql needs to be writable by laplace-runner (the systemd
    # unit's User=). Default ownership is postgres:postgres; setgid + group
    # add laplace-runner so both can write without sudo gymnastics.
    install -d -m 2775 -o postgres -g "$RUNNER_GROUP" "$LAPLACE_PG_SOCKET_DIR" 2>/dev/null \
        || install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$LAPLACE_PG_SOCKET_DIR"
    green "✓ $LAPLACE_PG_SOCKET_DIR writable by $RUNNER_GROUP"

    # systemd unit. Type=simple — our custom /opt/laplace/pgsql-18/bin/postgres
    # was built WITHOUT --with-systemd (it's the from-source build pinned to
    # the upstream REL_18_0 tag; sd_notify support requires explicit configure
    # flag which we don't pass). Without --with-systemd, postgres has no
    # sd_notify READY=1 to send → Type=notify deadlocks → systemd kills it
    # with status=1.
    # With logging_collector=on, postgres forks the log collector child but
    # doesn't itself daemonize — the main process stays as the postmaster
    # listening on the unix socket. Type=simple correctly tracks that.
    # ExecStart inherits the kernel's ld.so.cache (via bootstrap_engine_lib_path)
    # so /opt/laplace/{geos,proj,gdal}/lib take precedence — no
    # Environment=LD_LIBRARY_PATH needed.
    local unit_file="/etc/systemd/system/$LAPLACE_PG_SERVICE"
    cat > "$unit_file" <<EOF
[Unit]
Description=Laplace substrate PostgreSQL cluster (/opt/laplace/pgsql-18)
Documentation=https://github.com/SaltyPatron/Laplace
After=network.target
ConditionPathExists=$LAPLACE_PG_DATA/PG_VERSION

[Service]
Type=simple
User=$RUNNER_USER
Group=$RUNNER_GROUP
ExecStart=$LAPLACE_PG_PREFIX/bin/postgres -D $LAPLACE_PG_DATA
ExecReload=/bin/kill -HUP \$MAINPID
KillMode=mixed
KillSignal=SIGINT
TimeoutSec=120
Restart=on-failure
# Group-readable logs so developers can tail them without sudo (postgres
# forks the log collector before this UMask takes effect for already-
# created log files; the bootstrap also chmods the log dir g+r).
UMask=0027

[Install]
WantedBy=multi-user.target
EOF
    chmod 644 "$unit_file"
    systemctl daemon-reload
    green "✓ Installed $unit_file"

    if systemctl is-active --quiet "$LAPLACE_PG_SERVICE" 2>/dev/null; then
        systemctl restart "$LAPLACE_PG_SERVICE"
        green "✓ Restarted $LAPLACE_PG_SERVICE (picks up new conf + ld.so.cache)"
    else
        systemctl enable "$LAPLACE_PG_SERVICE" >/dev/null 2>&1 || true
        systemctl start "$LAPLACE_PG_SERVICE"
        green "✓ Enabled + started $LAPLACE_PG_SERVICE"
    fi

    # Wait for the socket to appear (Type=notify makes systemctl start block
    # until ready, but defensively poll the socket too).
    local tries=0
    while [ $tries -lt 30 ]; do
        if [ -S "$LAPLACE_PG_SOCKET_DIR/.s.PGSQL.$LAPLACE_PG_PORT" ]; then
            green "✓ Substrate cluster accepting connections on $LAPLACE_PG_SOCKET_DIR/.s.PGSQL.$LAPLACE_PG_PORT"
            return 0
        fi
        sleep 1
        tries=$((tries + 1))
    done
    red "✗ Substrate cluster failed to come up — check journalctl -u $LAPLACE_PG_SERVICE"
    return 1
}

bootstrap_pg_roles() {
    say "Ensure PG roles: laplace_admin (SUPERUSER) / laplace_app / laplace_readonly"
    # laplace_admin is a SUPERUSER. Rationale:
    #   * postgis is not a trusted extension — CREATE EXTENSION postgis
    #     requires SUPERUSER (PG docs). Without it laplace_admin can't
    #     install postgis, so we'd need either a SECURITY DEFINER wrapper
    #     (laplace_priv approach, accumulates per-DB state that dies with
    #     `just db-nuke`) or a custom template DB (more moving parts).
    #     Both are workarounds for the constraint that laplace_admin isn't
    #     a superuser.
    #   * For trusted extensions, contained-object ownership stays with
    #     the bootstrap superuser unless the install script explicitly
    #     does `ALTER ... OWNER TO`. Same problem class.
    #   * Standard PG pattern for a dedicated-cluster substrate is for the
    #     operator role to be a superuser. ahart already has OS sudo on
    #     this host (it's the dev box); making laplace_admin a SUPERUSER
    #     doesn't widen the attack surface — it just stops fighting PG's
    #     ownership / privilege model. AWS RDS uses rds_superuser, GCP
    #     Cloud SQL uses cloudsqlsuperuser; both are the same pattern.
    # laplace_app + laplace_readonly remain ordinary roles for future
    # least-privilege application + analyst access.
    # Connects to our substrate cluster via peer auth as laplace_admin
    # (initdb created the cluster with laplace_admin as the bootstrap
    # superuser; pg_hba + pg_ident grant ahart/laplace-runner OS users
    # peer access to that role).
    sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/psql" \
        -h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" \
        -d postgres -U laplace_admin -v ON_ERROR_STOP=1 <<'PG_EOF'
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_admin') THEN
        CREATE ROLE laplace_admin WITH LOGIN SUPERUSER CREATEDB CREATEROLE;
    ELSE
        ALTER ROLE laplace_admin WITH LOGIN SUPERUSER CREATEDB CREATEROLE;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        CREATE ROLE laplace_app WITH LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        CREATE ROLE laplace_readonly WITH LOGIN;
    END IF;
END $$;
PG_EOF
    green "✓ Roles present (laplace_admin = SUPERUSER; substrate operator role per AWS/GCP RDS pattern)"
}

bootstrap_pg_legacy_cleanup() {
    say "Clean up legacy 'ahart' PG state (substrate cluster)"
    sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/dropdb" \
        -h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" -U laplace_admin \
        --if-exists ahart 2>/dev/null \
        && green "✓ Dropped accidental 'ahart' database (if present)" \
        || green "✓ No 'ahart' database to drop"

    sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/psql" \
        -h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" \
        -d postgres -U laplace_admin \
        -c "DO \$\$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='ahart') THEN ALTER ROLE ahart NOSUPERUSER NOCREATEDB NOCREATEROLE; END IF; END \$\$;" >/dev/null 2>&1 \
        && green "✓ Revoked elevated privileges from 'ahart' PG role (if present)" \
        || green "✓ 'ahart' PG role already unprivileged (or absent)"
}

bootstrap_pg_database_and_postgis() {
    say "Ensure 'laplace' database owned by laplace_admin"

    # Single responsibility: ensure the laplace DB exists and is owned
    # by laplace_admin. That's it. Per-DB state (postgis, the laplace
    # schema + tables, role grants) is Layer-1 (DbUp) — laplace_admin
    # being a SUPERUSER means it can do all of that itself without any
    # wrapper, template, or sudo escalation in the iterative loop.
    #
    # `just db-nuke` drops + recreates the empty DB as laplace_admin (no
    # sudo). `just db-up` then runs `CREATE EXTENSION postgis;
    # CREATE EXTENSION laplace_geom; CREATE EXTENSION laplace_substrate;`
    # as laplace_admin, which is SUPERUSER, so all three install cleanly
    # and own their objects. No `laplace_priv` SECURITY DEFINER wrapper.
    # No custom `template_laplace`. No `ALTER OWNER` bandaids.

    local pgbin="$LAPLACE_PG_PREFIX/bin"
    local conn=(-h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" -U laplace_admin)

    if sudo -u "$RUNNER_USER" "$pgbin/psql" "${conn[@]}" -d postgres -tAc \
        "SELECT 1 FROM pg_database WHERE datname='laplace'" | grep -q 1; then
        green "✓ Database 'laplace' already exists (use 'just db-nuke' for a clean slate)"
    else
        sudo -u "$RUNNER_USER" "$pgbin/createdb" "${conn[@]}" -O laplace_admin laplace
        green "✓ Created database 'laplace' owned by laplace_admin"
    fi

    # CONNECT grants on the database for app + readonly roles. (Schema
    # USAGE is Layer 1's job since the laplace schema is created by
    # CREATE EXTENSION laplace_substrate, not here.)
    sudo -u "$RUNNER_USER" "$pgbin/psql" "${conn[@]}" -d laplace \
        -v ON_ERROR_STOP=1 >/dev/null <<'PG_EOF'
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        EXECUTE 'GRANT CONNECT ON DATABASE laplace TO laplace_app';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        EXECUTE 'GRANT CONNECT ON DATABASE laplace TO laplace_readonly';
    END IF;
END $$;
PG_EOF
    green "✓ CONNECT grants on laplace for laplace_app / laplace_readonly"

    # Clean up legacy template_laplace + per-DB laplace_priv if present
    # from prior bootstrap iterations that used wrapper/template patterns.
    # All harmless to remove now that laplace_admin is SUPERUSER and the
    # migration uses plain CREATE EXTENSION.
    sudo -u "$RUNNER_USER" "$pgbin/psql" "${conn[@]}" -d postgres \
        -v ON_ERROR_STOP=0 >/dev/null 2>&1 <<'PG_EOF'
UPDATE pg_database SET datistemplate = false WHERE datname = 'template_laplace';
DROP DATABASE IF EXISTS template_laplace;
PG_EOF
    sudo -u "$RUNNER_USER" "$pgbin/psql" "${conn[@]}" -d laplace \
        -v ON_ERROR_STOP=0 >/dev/null 2>&1 <<'PG_EOF'
DROP SCHEMA IF EXISTS laplace_priv CASCADE;
PG_EOF
    green "✓ Removed legacy template_laplace / laplace_priv (if present from prior bootstrap iterations)"
}

# bootstrap_pg_auth — superseded by bootstrap_laplace_pg_cluster, which
# writes pg_hba.conf + pg_ident.conf + postgresql.conf together as one
# managed block in $LAPLACE_PG_DATA/. Kept as a no-op shim so any
# external script invoking it directly doesn't break.
bootstrap_pg_auth() {
    green "✓ pg_hba.conf + pg_ident.conf maintained by bootstrap_laplace_pg_cluster"
}

# ---------------------------------------------------------------------------
# bootstrap_engine_lib_path — register /opt/laplace/lib with the dynamic
# linker so test binaries + the PG extension .so files find liblaplace_*.so
# automatically (no LD_LIBRARY_PATH dance, no rpath gymnastics, no being
# poisoned by stale .so files left in /usr/local/lib by a prior install).
# Idempotent — re-writing the same conf + re-running ldconfig is a no-op.
# ---------------------------------------------------------------------------
bootstrap_engine_lib_path() {
    say "Register /opt/laplace/lib + dep prefixes + Intel oneAPI runtime with ld.so.conf.d"
    local conf=/etc/ld.so.conf.d/laplace.conf
    # /opt/laplace/lib hosts the engine liblaplace_*.so files. The Intel
    # oneAPI runtime dir hosts libsvml.so, libirc.so, libimf.so etc., which
    # any .so compiled with icx/icpx DT_NEEDS at load. Without this entry
    # the `postgres` user (which doesn't source setvars.sh on login) gets
    # "libsvml.so: cannot open shared object file" the first time it tries
    # to dlopen a Laplace extension. The `latest` symlink in the Intel path
    # keeps this entry stable across oneAPI version bumps.
    #
    # /opt/laplace/{geos,proj,gdal}/lib host the from-source dep libraries
    # built via `just build-deps` (per ADR 0033 + 0038). Registering them
    # here, with laplace.conf processed BEFORE libc.conf and
    # x86_64-linux-gnu.conf (alphabetical .d order), makes ld.so.cache
    # prefer our libgeos.so.3.12.2 / libgeos_c.so.1.18.2 / libproj.so.25
    # / libgdal.so over the system 3.10.2 / older variants. This is
    # load-bearing for laplace_geom: liblwgeom (static-linked into our
    # extension) transitively references the GEOS C API; without ld.so
    # preferring our versions, the backend ends up with system libgeos
    # 3.10.2 in its address space and `CREATE EXTENSION laplace_geom`
    # fails with `undefined symbol: GEOSConcaveHullOfPolygons` (a
    # GEOS 3.11+ symbol). The same goes for postgis-3.so when running
    # against /opt/laplace/pgsql-18/lib/postgis-3.so (PostGIS 3.6.3
    # built against /opt/laplace/geos 3.12.2 — needs 3.11+ symbols).
    local intel_runtime=/opt/intel/oneapi/compiler/latest/lib
    local desired_lines=(
        "/opt/laplace/lib"
        "/opt/laplace/geos/lib"
        "/opt/laplace/proj/lib"
        "/opt/laplace/gdal/lib"
        "$intel_runtime"
    )
    local changed=0
    for line in "${desired_lines[@]}"; do
        if ! { [ -f "$conf" ] && grep -qxF "$line" "$conf"; }; then
            changed=1
            break
        fi
    done
    if [ "$changed" -eq 1 ]; then
        printf '%s\n' "${desired_lines[@]}" > "$conf"
        chmod 644 "$conf"
        green "✓ Wrote $conf"
        for line in "${desired_lines[@]}"; do
            echo "  $line"
        done
    else
        green "✓ $conf already lists /opt/laplace/lib + Intel runtime"
    fi
    ldconfig
    green "✓ Ran ldconfig"
}

# ---------------------------------------------------------------------------
# bootstrap_pg_extension_paths — tell the running system PG (PG_VERSION)
# to look for Laplace extensions and their engine .so deps under
# /opt/laplace, so `just install-laplace-prefix` is sudo-free and DbUp
# `CREATE EXTENSION laplace_geom; CREATE EXTENSION laplace_substrate;`
# finds the freshly-installed files.
#
# PG 18 caveat (per project memory project_pg18_extension_control_path):
# extension_control_path appends "/extension" to each custom entry — so
# pass the sharedir (.../share/postgresql/$PG_VERSION) NOT the extension
# subdir (.../share/postgresql/$PG_VERSION/extension). Get this wrong and
# CREATE EXTENSION silently loads a stale $system install instead.
#
# Also strips any pre-existing extension_control_path / dynamic_library_path
# lines, including stale entries pointing at older Hartonomous-* projects
# left over on this host. Idempotent — same desired-value writes are no-ops.
# ---------------------------------------------------------------------------
# bootstrap_pg_extension_paths — superseded by bootstrap_laplace_pg_cluster,
# which sets extension_control_path + dynamic_library_path in
# $PG_POSTGRESQL_CONF as part of the substrate cluster's managed config
# block. Kept as a no-op shim for callers that invoke it directly.
bootstrap_pg_extension_paths() {
    green "✓ extension_control_path + dynamic_library_path set by bootstrap_laplace_pg_cluster"
}

# ---------------------------------------------------------------------------
# bootstrap_cleanup_stale_installs — sweep system PG paths + /usr/local/lib
# of anything left by a prior `sudo cmake --install` of Laplace or an older
# project. Required because (a) until this script ran, ad-hoc installs
# landed there; (b) the new /opt/laplace-staged install layout means those
# files are now strictly stale; (c) leaving them around lets the loader /
# PG dlopen find the wrong copy on hosts where ld.so.cache reaches into
# /usr/local/lib first. This runs as part of bootstrap so the user
# doesn't accumulate "one more sudo command the agent forgot to script."
# Idempotent — silently absent is fine.
# ---------------------------------------------------------------------------
bootstrap_cleanup_stale_installs() {
    say "Remove stale Laplace + Hartonomous installs from system PG + /usr/local"
    local removed=0
    local f

    # Stale engine .so left in /usr/local/lib by a pre-/opt/laplace
    # `sudo cmake --install`. These now live at /opt/laplace/lib + are
    # registered with ld.so via bootstrap_engine_lib_path.
    for f in /usr/local/lib/liblaplace_*.so*; do
        [ -e "$f" ] || continue
        rm -f "$f" && removed=$((removed + 1))
    done

    # Stale extension .so left in system PG pkglibdir by a prior
    # `sudo cmake --install`. Includes the pre-ADR-0025 monolithic
    # `laplace.so` AND the split `laplace_{geom,substrate}.so`. New ones
    # live at /opt/laplace/lib/postgresql/$PG_VERSION via dynamic_library_path.
    for f in /usr/lib/postgresql/$PG_VERSION/lib/laplace.so \
             /usr/lib/postgresql/$PG_VERSION/lib/laplace_geom.so \
             /usr/lib/postgresql/$PG_VERSION/lib/laplace_substrate.so; do
        [ -e "$f" ] || continue
        rm -f "$f" && removed=$((removed + 1))
    done

    # Stale extension control + .sql files in system PG sharedir. Same
    # rationale; new ones live at /opt/laplace/share/postgresql/$PG_VERSION/extension.
    for f in /usr/share/postgresql/$PG_VERSION/extension/laplace.control \
             /usr/share/postgresql/$PG_VERSION/extension/laplace--*.sql \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_geom.control \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_geom--*.sql \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_substrate.control \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_substrate--*.sql; do
        [ -e "$f" ] || continue
        rm -f "$f" && removed=$((removed + 1))
    done

    # Hartonomous-* leftovers from the previous-iteration project (per
    # CLAUDE.md R-4 / RULES R11 — Hartonomous-001 is the predecessor and
    # must not bleed into this host). Removes the library, extension
    # files, and any extension-aux subdirs (e.g. /usr/share/postgresql/
    # $PG_VERSION/extension/hartonomous-ucd is a directory, not a file —
    # rm -rf so the glob handles both cases). Pre-Laplace; safe to nuke.
    for f in /usr/lib/postgresql/$PG_VERSION/lib/libhartonomous*.so* \
             /usr/share/postgresql/$PG_VERSION/extension/hartonomous*; do
        [ -e "$f" ] || continue
        rm -rf "$f" && removed=$((removed + 1))
    done

    if [ "$removed" -gt 0 ]; then
        ldconfig
        green "✓ Removed $removed stale install files; reran ldconfig"
    else
        green "✓ No stale install files to remove"
    fi
}

bootstrap_remove_legacy_sudoers() {
    # Removes /etc/sudoers.d/laplace-runner left over from prior bootstrap
    # iterations. The NOPASSWD-for-cmake-install entry was a workaround
    # for installing extensions into root-owned system PG paths. With
    # CMAKE_INSTALL_PREFIX=/opt/laplace (laplace-runner-owned, mode 2775)
    # plus extension_control_path / dynamic_library_path managed by
    # bootstrap_pg_extension_paths, the runner installs extensions to
    # /opt/laplace/share/postgresql/$PG_VERSION/extension and PG finds
    # them there — no sudo, no escalation, no NOPASSWD lying around.
    say "Remove legacy /etc/sudoers.d/laplace-runner (workaround no longer needed)"
    if [ -f "$SUDOERS_FILE" ]; then
        rm -f "$SUDOERS_FILE"
        green "✓ Removed $SUDOERS_FILE (cmake install no longer needs sudo)"
    else
        green "✓ $SUDOERS_FILE already absent"
    fi
}

bootstrap_external_dirs() {
    # Create /opt/laplace/external/ AND the per-dep install destinations
    # (tree-sitter, geos, proj, gdal, pgsql-18) with laplace-runner ownership
    # and setgid 2775 — so cmake --install / make install from the runner
    # (laplace-runner) can chmod the dirs it creates (must be owner) AND
    # ahart-in-laplace-runner-group can write through setgid+gw, without
    # collisions.
    #
    # /opt/laplace/external/ POPULATION is project state, not machine state —
    # it belongs in the pipeline. scripts/sync-external.sh maintains
    # /opt/laplace/external/<sm>/ as non-bare checkouts at .gitmodules-pinned
    # SHAs, idempotent, no sudo required.
    say "Ensure /opt/laplace/external/ + per-dep install destinations + engine install destinations"
    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" /opt/laplace/external
    # System-dep install destinations (per-dep cmake --install or make install lands here)
    for dep in tree-sitter geos proj gdal pgsql-18; do
        install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "/opt/laplace/$dep"
    done
    # Engine install destinations (top-level cmake --install build lands here:
    # liblaplace_*.so, headers, PG extension .so + .control + .sql).
    for sub in include lib share bin; do
        install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "/opt/laplace/$sub"
    done
    green "✓ /opt/laplace/{external,tree-sitter,geos,proj,gdal,pgsql-18,include,lib,share,bin}/ ready (owned $RUNNER_USER:$RUNNER_GROUP, mode 2775 setgid)"
    echo "  → /opt/laplace/external/ populated by: scripts/sync-external.sh (pipeline step or developer)"
}

bootstrap_runner_gh_auth() {
    # The runner user has no shell, so `gh auth login` is not possible.
    # Mirror $SUDO_USER's gh hosts.yml into the runner's $XDG_CONFIG_HOME
    # so the runner can invoke `gh issue edit`, `gh api`, etc. per the
    # CLAUDE.md cadence.
    #
    # Security note: this shares the invoking user's GitHub token with
    # the runner. For tighter separation, replace with a runner-specific
    # fine-grained PAT after bootstrap. The token's scope is whatever
    # the invoking user has minted; trust boundary is the local machine.
    say "Set up gh CLI auth for $RUNNER_USER (mirror $GH_SUDO_USER's token)"

    local src="/home/$GH_SUDO_USER/.config/gh/hosts.yml"
    if [ ! -f "$src" ]; then
        yellow "  $src not present — skipping (run 'gh auth login' as $GH_SUDO_USER first, then re-run bootstrap)"
        return
    fi

    local dst_dir="$RUNNER_HOME/.config/gh"
    local dst="$dst_dir/hosts.yml"
    install -d -m 700 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$RUNNER_HOME/.config"
    install -d -m 700 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$dst_dir"
    install -m 600 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$src" "$dst"
    green "✓ Mirrored $src -> $dst (mode 600, owned $RUNNER_USER)"

    # Sanity probe.
    if sudo -u "$RUNNER_USER" -H gh auth status >/dev/null 2>&1; then
        green "✓ $RUNNER_USER can authenticate with GitHub"
    else
        yellow "  gh auth status failed under $RUNNER_USER — token may have host-binding or scope issue"
    fi

    # Configure git's credential helper to use gh's token. This is what
    # makes `git clone https://github.com/...` authenticated (instead of
    # anonymous + rate-limited) for subsequent bootstrap_submodule_cache
    # runs as well as any workflow's git operations.
    if sudo -u "$RUNNER_USER" -H gh auth setup-git >/dev/null 2>&1; then
        green "✓ git credential helper wired to gh for $RUNNER_USER"
    else
        yellow "  gh auth setup-git failed under $RUNNER_USER — submodule clones will fall back to anonymous"
    fi
}

# ---------------------------------------------------------------------------
# do_status — what's the current state?
# ---------------------------------------------------------------------------
do_status() {
    say "User"
    id "$RUNNER_USER" 2>/dev/null || echo "  (absent)"

    say "Runner directory"
    if [ -d "$RUNNER_DIR" ]; then
        ls -la "$RUNNER_DIR" | head -5
    else
        echo "  (absent: $RUNNER_DIR)"
    fi

    say "Runner systemd service"
    systemctl status "$RUNNER_SERVICE" --no-pager 2>/dev/null | head -5 \
        || echo "  (absent: $RUNNER_SERVICE)"

    say "GitHub runner registration"
    sudo -u "$GH_SUDO_USER" -H gh api "repos/$REPO/actions/runners" \
        --jq '.runners[] | "\(.name) \(.status) labels=[\(.labels | map(.name) | join(","))]"' 2>/dev/null \
        || echo "  (gh CLI unavailable or unauthenticated for $GH_SUDO_USER)"

    say "Substrate cluster systemd service ($LAPLACE_PG_SERVICE)"
    systemctl status "$LAPLACE_PG_SERVICE" --no-pager 2>/dev/null | head -5 \
        || echo "  (absent: $LAPLACE_PG_SERVICE)"

    say "PG roles (substrate cluster)"
    sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/psql" \
        -h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" \
        -d postgres -U laplace_admin -tAc \
        "SELECT rolname, rolcanlogin, rolcreatedb, rolcreaterole, rolsuper
         FROM pg_roles WHERE rolname IN ('laplace_admin','laplace_app','laplace_readonly','ahart');" \
        2>/dev/null || echo "  (substrate cluster not running, or no roles set up)"

    say "Peer auth in pg_hba (substrate cluster)"
    grep "laplace_admin" "$PG_HBA_FILE" 2>/dev/null || echo "  (no laplace_admin entry)"
    say "Peer auth map in pg_ident (substrate cluster)"
    grep "laplace_map" "$PG_IDENT_FILE" 2>/dev/null || echo "  (no laplace_map entry)"

    say "Sudoers"
    if [ -f "$SUDOERS_FILE" ]; then
        cat "$SUDOERS_FILE"
    else
        echo "  (absent: $SUDOERS_FILE)"
    fi

    say "Layer-1 state (databases in substrate cluster)"
    sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/psql" \
        -h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" \
        -d postgres -U laplace_admin -tAc \
        "SELECT datname FROM pg_database WHERE datname IN ('laplace','ahart');" \
        2>/dev/null || echo "  (substrate cluster unavailable)"
}

# ---------------------------------------------------------------------------
# do_reset — undo everything bootstrap_* sets up, in reverse order
# ---------------------------------------------------------------------------
do_reset() {
    cat <<EOF
========================================================================
RESET — Layer 0 tear-down
========================================================================
This will remove:
  - GitHub runner registration for 'hart-server' on $REPO
  - systemd service $RUNNER_SERVICE
  - $RUNNER_DIR
  - PG roles laplace_admin / laplace_app / laplace_readonly
    (and any databases they own — including 'laplace' if present)
  - pg_hba.conf + pg_ident.conf entries for laplace
  - $SUDOERS_FILE
  - System account $RUNNER_USER and $RUNNER_HOME

This does NOT touch:
  - The 'postgres' user, postgres OS daemon, or other databases
  - The 'ahart' OS user or its home directory
  - The 'ahart' PG role (only any leftover privileges if changed earlier)

EOF
    read -rp "Type 'RESET' to confirm: " confirm
    if [ "$confirm" != "RESET" ]; then
        yellow "Aborted (input was '$confirm', not 'RESET')."
        exit 1
    fi

    # --- Reverse step 8: tear down runner service + GitHub registration ---
    say "Stop + uninstall runner service"
    systemctl stop "$RUNNER_SERVICE" 2>/dev/null || true
    systemctl disable "$RUNNER_SERVICE" 2>/dev/null || true
    if [ -x "$RUNNER_DIR/svc.sh" ]; then
        (cd "$RUNNER_DIR" && ./svc.sh uninstall 2>/dev/null) || true
    fi
    green "✓ Service stopped"

    say "Deregister runner from GitHub"
    local token
    token=$(mint_gh_token "remove-token")
    if [ -n "$token" ] && [ -x "$RUNNER_DIR/config.sh" ]; then
        (cd "$RUNNER_DIR" && sudo -u "$RUNNER_USER" -H ./config.sh remove --token "$token" 2>/dev/null) || true
        green "✓ Deregistered from GitHub"
    else
        yellow "Could not deregister via gh CLI; if a 'hart-server' runner persists in repo settings, remove it manually"
    fi

    # --- Reverse step 7: sudoers ---
    say "Remove sudoers"
    rm -f "$SUDOERS_FILE"
    green "✓ $SUDOERS_FILE removed"

    # --- Reverse steps 5+6: tear down substrate cluster (roles, DBs, data dir, unit) ---
    say "Stop + disable substrate cluster systemd unit ($LAPLACE_PG_SERVICE)"
    if systemctl list-unit-files "$LAPLACE_PG_SERVICE" 2>/dev/null | grep -q "$LAPLACE_PG_SERVICE"; then
        systemctl stop "$LAPLACE_PG_SERVICE" 2>/dev/null || true
        systemctl disable "$LAPLACE_PG_SERVICE" 2>/dev/null || true
        rm -f "/etc/systemd/system/$LAPLACE_PG_SERVICE"
        systemctl daemon-reload
        green "✓ Removed $LAPLACE_PG_SERVICE"
    else
        green "✓ $LAPLACE_PG_SERVICE not installed"
    fi

    say "Remove substrate cluster data dir + logs"
    if [ -d "$LAPLACE_PG_DATA" ]; then
        rm -rf "$LAPLACE_PG_DATA"
        green "✓ Removed $LAPLACE_PG_DATA (cluster wiped)"
    fi
    if [ -d "$LAPLACE_PG_PREFIX/log" ]; then
        rm -rf "$LAPLACE_PG_PREFIX/log"
        green "✓ Removed $LAPLACE_PG_PREFIX/log"
    fi

    say "Re-enable system postgresql@$PG_VERSION-main (if installed) so the host has a default PG"
    local sys_unit="postgresql@${PG_VERSION}-main.service"
    if systemctl list-unit-files "$sys_unit" 2>/dev/null | grep -q "$sys_unit"; then
        systemctl enable "$sys_unit" 2>/dev/null || true
        green "✓ Re-enabled $sys_unit (start it manually if you want it back)"
    fi

    # --- Reverse steps 1–4: runner dir + user ---
    say "Remove runner installation"
    if [ -d "$RUNNER_DIR" ]; then
        rm -rf "$RUNNER_DIR"
        green "✓ Removed $RUNNER_DIR"
    fi
    if [ -d "$RUNNER_HOME" ]; then
        rm -rf "$RUNNER_HOME"
        green "✓ Removed $RUNNER_HOME"
    fi
    # Also remove the legacy /var/lib/laplace-runner path in case a
    # pre-migration bootstrap is being reset.
    if [ -d "$RUNNER_HOME_LEGACY" ] && [ "$RUNNER_HOME_LEGACY" != "$RUNNER_HOME" ]; then
        rm -rf "$RUNNER_HOME_LEGACY"
        green "✓ Removed legacy $RUNNER_HOME_LEGACY"
    fi
    # And any pre-migration archives left behind.
    for archive in "$RUNNER_HOME_LEGACY".pre-migration-*; do
        [ -e "$archive" ] || continue
        rm -rf "$archive"
        green "✓ Removed migration archive $archive"
    done

    say "Remove system account $RUNNER_USER"
    if id -u "$RUNNER_USER" >/dev/null 2>&1; then
        userdel "$RUNNER_USER" 2>/dev/null || true
        groupdel "$RUNNER_GROUP" 2>/dev/null || true
        green "✓ Removed user + group $RUNNER_USER"
    else
        green "✓ User $RUNNER_USER already absent"
    fi

    echo
    green "===== RESET COMPLETE ====="
    echo "To rebuild: sudo $0 bootstrap"
}

# ---------------------------------------------------------------------------
# do_bootstrap — full Layer 0 set-up
# ---------------------------------------------------------------------------
do_bootstrap() {
    bootstrap_user
    bootstrap_build_environment
    bootstrap_migrate_runner_home
    bootstrap_legacy_runner_teardown
    bootstrap_runner_install
    bootstrap_runner_register
    bootstrap_runner_service

    # ld.so.cache must list /opt/laplace/{geos,proj,gdal}/lib BEFORE our
    # substrate cluster starts — otherwise the postgres binary forks
    # backends that resolve libgeos/libproj to system 3.10.2 / 8.x first
    # and segfault at backend exit (verified via core dump 2026-05-24:
    # libproj.so.22's UnitOfMeasure destructor double-frees state
    # libproj.so.25 already freed when both ABIs are loaded).
    bootstrap_engine_lib_path
    # /opt/laplace/external/ + per-dep install destinations need correct
    # ownership before the cluster's data dir is initdb'd (which lives
    # under /opt/laplace/pgsql-18/).
    bootstrap_external_dirs

    # Stand up the substrate PG cluster: stop system PG, initdb
    # /opt/laplace/pgsql-18/data as laplace-runner, write managed
    # conf/hba/ident blocks, install + start systemd unit. Owns
    # /var/run/postgresql/.s.PGSQL.5432 — same shape as system PG
    # so all existing psql/DbUp connections keep working.
    bootstrap_disable_system_postgresql
    bootstrap_laplace_pg_cluster

    # PG roles + database now operate against the substrate cluster
    # (peer auth via laplace_map, as laplace-runner → laplace_admin).
    bootstrap_pg_roles
    bootstrap_pg_legacy_cleanup
    bootstrap_pg_auth                # no-op shim — cluster owns its own conf
    bootstrap_pg_database_and_postgis

    # Cleanup runs AFTER the substrate cluster is up but BEFORE any
    # downstream operations might depend on the cleaned state.
    bootstrap_cleanup_stale_installs
    bootstrap_pg_extension_paths     # no-op shim — cluster owns its own conf
    bootstrap_remove_legacy_sudoers
    bootstrap_runner_gh_auth

    say "Verification"
    echo "Runner service:"
    systemctl status "$RUNNER_SERVICE" --no-pager 2>/dev/null | head -8 \
        || red "✗ Runner service not active"

    echo
    echo "GitHub registration:"
    sudo -u "$GH_SUDO_USER" -H gh api "repos/$REPO/actions/runners" \
        --jq '.runners[] | "\(.name) \(.status) labels=[\(.labels | map(.name) | join(","))]"' 2>/dev/null || true

    # Peer auth flow: pg_hba.conf rule applies to the *requested* PG role
    # (laplace_admin), and pg_ident.conf's laplace_map allows OS user
    # laplace-runner (or ahart) to fulfill that request. So we MUST pass
    # -U laplace_admin explicitly — psql's default of (PG role = OS user)
    # would request role `laplace-runner`, which doesn't exist as a PG role.
    echo
    echo "Peer auth (OS laplace-runner → PG laplace_admin on 'postgres' DB):"
    sudo -u "$RUNNER_USER" psql -d postgres -U laplace_admin -tAc \
        "SELECT current_user || ' on ' || current_database();" 2>&1 \
        | sed 's/^/  → /'

    echo
    echo "Peer auth (OS ahart → PG laplace_admin on 'postgres' DB):"
    sudo -u ahart psql -d postgres -U laplace_admin -tAc \
        "SELECT current_user || ' on ' || current_database();" 2>&1 \
        | sed 's/^/  → /'

    echo
    echo "Sudoers entry (laplace-runner NOPASSWD for /usr/bin/cmake --install + legacy /usr/bin/make install*):"
    # Use sudo -ln (list allowed commands, non-interactive) so we don't have
    # to invoke a command that matches the allowed pattern. sudo -n alone
    # would attempt to authorize a specific command and fail under pipefail
    # if the command doesn't match the sudoers pattern — that's a probe bug,
    # not a real failure.
    if sudo -u "$RUNNER_USER" -H sudo -ln 2>&1 | grep -qE 'NOPASSWD.*(cmake --install|make install)'; then
        sudo -u "$RUNNER_USER" -H sudo -ln 2>&1 | grep -E 'NOPASSWD.*make' \
            | sed 's/^/  → /'
        green "✓ Sudoers rule active"
    else
        red "✗ Sudoers rule NOT active for laplace-runner"
    fi

    echo
    green "===== LAYER 0 BOOTSTRAP COMPLETE ====="
    echo
    echo "Layer 0 (this script) is done."
    echo
    echo "Layer 1 (database + extensions + schema) — run from the repo root:"
    echo "  just db-up         # EnsureDatabase('laplace') + apply migrations"
    echo "                     # → CREATE EXTENSION postgis + laplace, role grants"
    echo "                     # → substrate schema flows in via the extension"
    echo
    echo "Layer 2 (CI verification) — push to main, or:"
    echo "  gh workflow run integration.yml"
    echo
    echo "Layer 1 reset (start fresh without touching Layer 0):"
    echo "  just db-nuke       # DROP DATABASE laplace, re-create empty"
    echo
    echo "Full reset (undo Layer 0):"
    echo "  sudo $0 reset"
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
case "$MODE" in
    bootstrap)
        require_root
        do_bootstrap
        ;;
    reset)
        require_root
        do_reset
        ;;
    status)
        do_status
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
