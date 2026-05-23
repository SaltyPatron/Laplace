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
#  12. /opt/laplace/external/ — canonical persistent clone of every submodule
#      in .gitmodules. Cloned once from upstream, fetched on subsequent runs.
#      Used as --reference target by scripts/ci-init-submodules.sh so the
#      runner workspace never re-downloads 5+ GB of submodule sources between
#      workflow runs. Owned ahart:laplace-runner setgid (matches /opt/laplace).
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
PG_CONFIG_DIR="/etc/postgresql/$PG_VERSION/main"
REPO="SaltyPatron/Laplace"
REPO_URL="https://github.com/$REPO"
RUNNER_VERSION="v2.334.0"
RUNNER_TARBALL="actions-runner-linux-x64-${RUNNER_VERSION#v}.tar.gz"
RUNNER_DL_URL="https://github.com/actions/runner/releases/download/$RUNNER_VERSION/$RUNNER_TARBALL"
SUDOERS_FILE="/etc/sudoers.d/laplace-runner"
PG_HBA_FILE="$PG_CONFIG_DIR/pg_hba.conf"
PG_IDENT_FILE="$PG_CONFIG_DIR/pg_ident.conf"
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
    # the laplace-runner group. Both /opt/laplace (mode 2775) AND the
    # app/Laplace.*/{obj,bin} pre-created by setup-host's layer1 (mode 775,
    # owned by laplace-runner:laplace-runner) rely on this group membership
    # for `just db-up` / `just test-app` / etc. running as the dev user to
    # write into laplace-runner-owned files via group perms instead of
    # needing sudo per iteration. The CI runner (running as laplace-runner)
    # writes via owner perms. Idempotent — adding to a group you're already
    # in is a no-op.
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

    # Custom-build install prefix. Owned by the invoking user ($GH_SUDO_USER —
    # typically ahart) with laplace-runner as the group, so:
    #   - The developer (and the AGENT running as them) write /opt/laplace
    #     directly via owner permissions — no sudo, no group-membership shell
    #     restart dance.
    #   - The systemd-run CI runner (running as laplace-runner) writes via
    #     group permissions.
    #   - The setgid bit (2775) ensures new files inherit the laplace-runner
    #     group, preserving CI access regardless of who creates them.
    # Without this ownership shape `just build-deps` / `just install-laplace-prefix`
    # fail at the install step because the developer has no /opt/laplace write
    # access — which was the bug that caused two sessions of sudo-prompt churn
    mkdir -p /opt/laplace
    chown -R "$GH_SUDO_USER:$RUNNER_GROUP" /opt/laplace
    chmod -R 2775 /opt/laplace
    green "✓ /opt/laplace owned by $GH_SUDO_USER:$RUNNER_GROUP (mode 2775 — owner + group writable, setgid)"
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
    sudo -u postgres psql -v ON_ERROR_STOP=1 <<'PG_EOF'
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
    say "Clean up legacy 'ahart' PG state"
    sudo -u postgres dropdb --if-exists ahart 2>/dev/null \
        && green "✓ Dropped accidental 'ahart' database" \
        || green "✓ No 'ahart' database to drop"

    sudo -u postgres psql -c "ALTER ROLE ahart NOSUPERUSER NOCREATEDB NOCREATEROLE;" 2>/dev/null \
        && green "✓ Revoked elevated privileges from 'ahart' PG role" \
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

    if sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='laplace'" | grep -q 1; then
        green "✓ Database 'laplace' already exists (use 'just db-nuke' for a clean slate)"
    else
        sudo -u postgres createdb -O laplace_admin laplace
        green "✓ Created database 'laplace' owned by laplace_admin"
    fi

    # CONNECT grants on the database for app + readonly roles. (Schema
    # USAGE is Layer 1's job since the laplace schema is created by
    # CREATE EXTENSION laplace_substrate, not here.)
    sudo -u postgres psql -d laplace -v ON_ERROR_STOP=1 <<'PG_EOF' >/dev/null
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
    sudo -u postgres psql -d postgres -v ON_ERROR_STOP=0 <<'PG_EOF' >/dev/null 2>&1
UPDATE pg_database SET datistemplate = false WHERE datname = 'template_laplace';
DROP DATABASE IF EXISTS template_laplace;
PG_EOF
    sudo -u postgres psql -d laplace -v ON_ERROR_STOP=0 <<'PG_EOF' >/dev/null 2>&1
DROP SCHEMA IF EXISTS laplace_priv CASCADE;
PG_EOF
    green "✓ Removed legacy template_laplace / laplace_priv (if present from prior bootstrap iterations)"
}

bootstrap_pg_auth() {
    say "Configure peer auth (pg_ident.conf + pg_hba.conf)"

    if ! grep -q "^laplace_map" "$PG_IDENT_FILE" 2>/dev/null; then
        cat >> "$PG_IDENT_FILE" <<EOF

# Laplace runner mapping — maps OS users to PG role laplace_admin
laplace_map   laplace-runner   laplace_admin
laplace_map   ahart            laplace_admin
EOF
        green "✓ Added laplace_map to $PG_IDENT_FILE"
    else
        green "✓ laplace_map already present in $PG_IDENT_FILE"
    fi

    if ! grep -qE "laplace_admin.*peer.*map=laplace_map" "$PG_HBA_FILE" 2>/dev/null; then
        awk -v line="local   all             laplace_admin                           peer map=laplace_map" '
            /^# TYPE/ && !done { print; print line; done=1; next }
            { print }
        ' "$PG_HBA_FILE" > "${PG_HBA_FILE}.new"
        mv "${PG_HBA_FILE}.new" "$PG_HBA_FILE"
        chown postgres:postgres "$PG_HBA_FILE"
        chmod 640 "$PG_HBA_FILE"
        green "✓ Added laplace_admin peer auth to $PG_HBA_FILE"
    else
        green "✓ laplace_admin peer auth already present in $PG_HBA_FILE"
    fi

    systemctl reload postgresql
    green "✓ Reloaded PostgreSQL"
}

# ---------------------------------------------------------------------------
# bootstrap_engine_lib_path — register /opt/laplace/lib with the dynamic
# linker so test binaries + the PG extension .so files find liblaplace_*.so
# automatically (no LD_LIBRARY_PATH dance, no rpath gymnastics, no being
# poisoned by stale .so files left in /usr/local/lib by a prior install).
# Idempotent — re-writing the same conf + re-running ldconfig is a no-op.
# ---------------------------------------------------------------------------
bootstrap_engine_lib_path() {
    say "Register /opt/laplace/lib + Intel oneAPI runtime with ld.so.conf.d"
    local conf=/etc/ld.so.conf.d/laplace.conf
    # /opt/laplace/lib hosts the engine liblaplace_*.so files. The Intel
    # oneAPI runtime dir hosts libsvml.so, libirc.so, libimf.so etc., which
    # any .so compiled with icx/icpx DT_NEEDS at load. Without this entry
    # the `postgres` user (which doesn't source setvars.sh on login) gets
    # "libsvml.so: cannot open shared object file" the first time it tries
    # to dlopen a Laplace extension. The `latest` symlink in the Intel path
    # keeps this entry stable across oneAPI version bumps.
    local intel_runtime=/opt/intel/oneapi/compiler/latest/lib
    local desired_lines=(
        "/opt/laplace/lib"
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
bootstrap_pg_extension_paths() {
    say "Set extension_control_path + dynamic_library_path in postgresql.conf"
    local conf="$PG_CONFIG_DIR/postgresql.conf"
    local marker_begin="# >>> laplace-runner managed: extension paths >>>"
    local marker_end="# <<< laplace-runner managed: extension paths <<<"
    local desired_ecp="extension_control_path = '/opt/laplace/share/postgresql/$PG_VERSION:\$system'"
    local desired_dlp="dynamic_library_path = '\$libdir:/opt/laplace/lib/postgresql/$PG_VERSION'"

    # Strip prior managed block (between begin/end markers) AND any loose
    # GUC lines anywhere else in the file (so an older non-marker write
    # or a hand-edit can't shadow this one). Idempotent across re-runs.
    sed -i \
        -e "/$marker_begin/,/$marker_end/d" \
        -e '/^[[:space:]]*#\?[[:space:]]*extension_control_path[[:space:]]*=/d' \
        -e '/^[[:space:]]*#\?[[:space:]]*dynamic_library_path[[:space:]]*=/d' \
        "$conf"

    cat >> "$conf" <<EOF

$marker_begin
# Lets the running PG find Laplace extensions installed to /opt/laplace
# without sudo per ADR 0019 (laplace-runner-owned prefix). PG 18 appends
# '/extension' to each custom extension_control_path entry, so the value
# here is the SHAREDIR, not the extension subdir. dynamic_library_path
# resolves the extensions' .so files (which in turn DT_NEEDED the engine
# liblaplace_*.so files registered via /etc/ld.so.conf.d/laplace.conf).
$desired_ecp
$desired_dlp
$marker_end
EOF
    chown postgres:postgres "$conf"
    green "✓ Updated $conf (extension_control_path + dynamic_library_path)"

    systemctl reload postgresql
    green "✓ Reloaded PostgreSQL (new paths active without restart)"
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

bootstrap_submodule_cache() {
    # Per ADR 0033 — all C/C++ deps are submodules under external/. The
    # runner workspace is scratch (actions/checkout cleans it); to keep
    # CI from re-downloading 5+ GB of submodule sources every workflow
    # run, /opt/laplace/external/ holds one persistent canonical clone
    # per submodule. scripts/ci-init-submodules.sh points each workspace
    # submodule init at this cache via git's --reference / alternates.
    #
    # First run: clones every submodule URL from .gitmodules. Network-
    # bound; ~10-20 min depending on link speed (~530 MB checked out per
    # ADR 0033 estimate; ~1 GB with all 303 tree-sitter grammars).
    # Subsequent runs: `git fetch` in each existing clone — sub-minute.
    #
    # Runs as laplace-runner so ownership lands right from the start.
    # Files inherit the laplace-runner group via /opt/laplace's setgid.
    say "Populate /opt/laplace/external/ submodule cache"

    local repo_root
    repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    if [ ! -f "$repo_root/.gitmodules" ]; then
        red "Expected .gitmodules at $repo_root — run bootstrap from the Laplace repo"
        exit 1
    fi

    local cache_root="/opt/laplace/external"
    install -d -m 2775 -o "$GH_SUDO_USER" -g "$RUNNER_GROUP" "$cache_root"

    # Group-write umask: new files come out mode 664, new dirs 2775.
    # Combined with /opt/laplace's setgid + laplace-runner group, this
    # means ahart (group member) AND laplace-runner (group member) can
    # both update the cache without sudo / user-switching.
    umask 002

    # Total count for progress display.
    local total
    total=$(grep -c '^\[submodule' "$repo_root/.gitmodules")
    echo "  total submodules to process: $total"

    local count_cloned=0 count_fetched=0 count_failed=0 count_seen=0
    local last_progress=0
    # Enumerate (path, url) pairs from .gitmodules.
    while IFS=$'\t' read -r path url; do
        [ -n "$path" ] && [ -n "$url" ] || continue
        count_seen=$((count_seen + 1))
        # .gitmodules paths start with "external/" (the in-repo location).
        # Strip that prefix so cache entries land directly under the
        # cache root, i.e. /opt/laplace/external/<name>/ — not the
        # absurd /opt/laplace/external/external/<name>/.
        local rel="${path#external/}"
        local target="$cache_root/$rel"
        if [ -d "$target/.git" ]; then
            # Group-writable cache + setgid → no user-switching needed.
            if git -C "$target" fetch --quiet --tags \
                origin '+refs/heads/*:refs/remotes/origin/*' 2>/dev/null; then
                count_fetched=$((count_fetched + 1))
            else
                count_failed=$((count_failed + 1))
                yellow "  [$count_seen/$total] fetch failed: $path"
            fi
        else
            # Parent dir for nested paths (e.g. tree-sitter-grammars/<lang>).
            # setgid inherits laplace-runner group from /opt/laplace.
            mkdir -p "$(dirname "$target")"
            if git clone --quiet "$url" "$target" 2>/dev/null; then
                count_cloned=$((count_cloned + 1))
            else
                count_failed=$((count_failed + 1))
                yellow "  [$count_seen/$total] clone failed: $path ($url)"
            fi
        fi
        # Progress heartbeat every 25 submodules so the operator sees
        # the loop is making progress (relevant on first-time runs that
        # take 10-20 min cloning ~300 small grammar repos).
        if [ $((count_seen - last_progress)) -ge 25 ]; then
            printf "  [%d/%d] cloned=%d fetched=%d failed=%d\n" \
                "$count_seen" "$total" "$count_cloned" "$count_fetched" "$count_failed"
            last_progress=$count_seen
        fi
    done < <(
        awk '
            /^\[submodule/ { in_sm=1; path=""; url=""; next }
            in_sm && /^[[:space:]]*path[[:space:]]*=/ { sub(/^[^=]*=[[:space:]]*/,""); path=$0 }
            in_sm && /^[[:space:]]*url[[:space:]]*=/  { sub(/^[^=]*=[[:space:]]*/,""); url=$0 }
            in_sm && path != "" && url != "" { printf "%s\t%s\n", path, url; in_sm=0 }
        ' "$repo_root/.gitmodules"
    )

    green "✓ submodule cache: cloned=$count_cloned fetched=$count_fetched failed=$count_failed under $cache_root"
    if [ "$count_failed" -gt 0 ]; then
        yellow "  (failures don't abort bootstrap — re-run to retry; or fix individual upstream issues)"
    fi
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

    say "PG roles"
    sudo -u postgres psql -tAc \
        "SELECT rolname, rolcanlogin, rolcreatedb, rolcreaterole, rolsuper
         FROM pg_roles WHERE rolname IN ('laplace_admin','laplace_app','laplace_readonly','ahart');" \
        2>/dev/null || echo "  (postgres not running, or no roles set up)"

    say "Peer auth in pg_hba"
    grep "laplace_admin" "$PG_HBA_FILE" 2>/dev/null || echo "  (no laplace_admin entry)"
    say "Peer auth map in pg_ident"
    grep "laplace_map" "$PG_IDENT_FILE" 2>/dev/null || echo "  (no laplace_map entry)"

    say "Sudoers"
    if [ -f "$SUDOERS_FILE" ]; then
        cat "$SUDOERS_FILE"
    else
        echo "  (absent: $SUDOERS_FILE)"
    fi

    say "Layer-1 state (databases)"
    sudo -u postgres psql -tAc "SELECT datname FROM pg_database WHERE datname IN ('laplace','ahart');" \
        2>/dev/null || echo "  (postgres unavailable)"
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

    # --- Reverse step 6: pg_hba + pg_ident ---
    say "Remove peer-auth entries from pg_hba.conf + pg_ident.conf"
    if [ -f "$PG_HBA_FILE" ]; then
        sed -i '/laplace_admin.*peer.*map=laplace_map/d' "$PG_HBA_FILE"
        green "✓ Stripped laplace_admin peer-auth lines from $PG_HBA_FILE"
    fi
    if [ -f "$PG_IDENT_FILE" ]; then
        # Remove the whole Laplace block (comment + two map lines)
        sed -i '/# Laplace runner mapping/,/^laplace_map.*ahart.*laplace_admin/d' "$PG_IDENT_FILE"
        # Defensive: also remove any orphaned laplace_map lines
        sed -i '/^laplace_map/d' "$PG_IDENT_FILE"
        green "✓ Stripped laplace_map from $PG_IDENT_FILE"
    fi
    systemctl reload postgresql 2>/dev/null || true

    # --- Reverse step 5: PG roles + any DBs they own ---
    say "Drop laplace database (if present) + PG roles"
    sudo -u postgres dropdb --if-exists laplace 2>/dev/null \
        && green "✓ Dropped 'laplace' database" \
        || green "✓ No 'laplace' database to drop"

    sudo -u postgres psql -v ON_ERROR_STOP=0 <<'PG_EOF'
-- REASSIGN/DROP OWNED is the safe way to drop a role with grants in any DB.
-- These DOs handle the case where the role doesn't exist.
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        DROP OWNED BY laplace_readonly CASCADE;
        DROP ROLE laplace_readonly;
    END IF;
END $$;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        DROP OWNED BY laplace_app CASCADE;
        DROP ROLE laplace_app;
    END IF;
END $$;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_admin') THEN
        DROP OWNED BY laplace_admin CASCADE;
        DROP ROLE laplace_admin;
    END IF;
END $$;
PG_EOF
    green "✓ PG roles dropped (with OWNED objects)"

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
    bootstrap_pg_roles
    bootstrap_pg_legacy_cleanup
    bootstrap_pg_auth
    bootstrap_pg_database_and_postgis
    # Cleanup runs BEFORE the engine_lib_path ldconfig so we don't get
    # the "libhartonomous.so.0 is not a symbolic link" warning on the
    # first ldconfig of a re-bootstrap (the sweep removes the offending
    # file; engine_lib_path's subsequent ldconfig then sees a clean tree).
    bootstrap_cleanup_stale_installs
    bootstrap_engine_lib_path
    bootstrap_pg_extension_paths
    bootstrap_remove_legacy_sudoers
    bootstrap_runner_gh_auth      # before submodule cache so clones are authenticated
    bootstrap_submodule_cache

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
