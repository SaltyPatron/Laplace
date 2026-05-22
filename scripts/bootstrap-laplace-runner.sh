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
#   1. System account `laplace-runner` (no home in /home, no shell)
#   2. GitHub Actions runner installed at /var/lib/laplace-runner/actions-runner
#   3. systemd service running as laplace-runner
#   4. PG roles laplace_admin / laplace_app / laplace_readonly
#   5. pg_ident.conf  → maps OS user laplace-runner (and ahart, for interactive
#      dev) onto PG role laplace_admin
#   6. pg_hba.conf    → peer auth for laplace_admin via that map
#   7. /etc/sudoers.d/laplace-runner — bounded NOPASSWD for `make install*`
#      so the runner can install the extension into PG's extension dirs
#   8. Cleanup of legacy state (ahart-as-superuser, ahart DB, old runner
#      under /home/ahart/actions-runner)
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
RUNNER_HOME="/var/lib/laplace-runner"
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
    say "Ensure PG roles: laplace_admin / laplace_app / laplace_readonly"
    sudo -u postgres psql -v ON_ERROR_STOP=1 <<'PG_EOF'
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_admin') THEN
        CREATE ROLE laplace_admin WITH LOGIN CREATEDB CREATEROLE;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_app') THEN
        CREATE ROLE laplace_app WITH LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'laplace_readonly') THEN
        CREATE ROLE laplace_readonly WITH LOGIN;
    END IF;
END $$;
PG_EOF
    green "✓ Roles present (CREATEDB on laplace_admin so Layer 1 can EnsureDatabase)"
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
    say "Ensure 'laplace' database + laplace_priv schema + postgis"

    # ---------------------------------------------------------------
    # (1) Database — owned by laplace_admin (CREATEDB + DB-owner means
    #     laplace_admin can later DROP + recreate without superuser).
    # ---------------------------------------------------------------
    if sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='laplace'" | grep -q 1; then
        green "✓ Database 'laplace' already exists"
    else
        sudo -u postgres createdb -O laplace_admin laplace
        green "✓ Created database 'laplace' owned by laplace_admin"
    fi

    # ---------------------------------------------------------------
    # (2) laplace_priv schema + SECURITY DEFINER wrappers.
    #
    #     laplace_admin is intentionally NOT a cluster-wide SUPERUSER.
    #     To give it "full control of the laplace database" (incl.
    #     installing extensions that normally require superuser, like
    #     postgis), we expose narrow, allowlist-bounded SECURITY DEFINER
    #     functions owned by postgres. The functions:
    #
    #       - run with postgres's privileges (CREATE EXTENSION works)
    #       - reject names outside the allowlist
    #       - reject calls from any DB other than 'laplace'
    #
    #     Result: laplace_admin can manage extensions in laplace via
    #     SELECT laplace_priv.install_extension('postgis'), but cannot
    #     escape to alter postgres-DB / drop other databases / install
    #     arbitrary superuser-required extensions.
    #
    #     Idempotent (CREATE OR REPLACE). Re-runnable after db-nuke
    #     because the schema lives in the laplace DB.
    # ---------------------------------------------------------------
    sudo -u postgres psql -d laplace -v ON_ERROR_STOP=1 >/dev/null <<'PG_EOF'
CREATE SCHEMA IF NOT EXISTS laplace_priv AUTHORIZATION postgres;
GRANT USAGE ON SCHEMA laplace_priv TO laplace_admin;
REVOKE CREATE ON SCHEMA laplace_priv FROM laplace_admin;
REVOKE CREATE ON SCHEMA laplace_priv FROM PUBLIC;

CREATE OR REPLACE FUNCTION laplace_priv.install_extension(ext_name text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_catalog
AS $func$
BEGIN
    -- Allowlist — substrate-honest only. See
    -- ~/.claude/projects/.../memory/feedback_conventional_db_reflex.md
    -- for why pg_trgm/intarray/citext/unaccent/bloom are NOT here even
    -- though "they sound useful". The substrate replaces those.
    --
    -- search_path is set to (public, pg_catalog) — NOT (pg_catalog, public).
    -- Reason: extension install scripts that create objects without a schema
    -- qualifier (e.g., postgis's `CREATE TYPE geometry_dump AS (...)`) resolve
    -- to the FIRST writable schema in search_path. With pg_catalog first,
    -- PG tries to create in pg_catalog and fails (even SUPERUSER can't easily
    -- create into pg_catalog at extension-install time). With public first,
    -- extension objects land in public as intended.
    IF ext_name NOT IN (
        -- Geometry (the substrate extends PostGIS, ADR 0001)
        'postgis', 'postgis_topology', 'postgis_raster', 'postgis_sfcgal',
        -- GIST/GIN composition with scalar predicates alongside geometry
        'btree_gist', 'btree_gin',
        -- Crypto primitives for future signed-attestation envelopes
        'pgcrypto',
        -- Observability — required to tune the cascade
        'pg_stat_statements', 'auto_explain', 'pg_buffercache',
        'pg_prewarm', 'pg_visibility',
        -- Future-substrate federation across hosts
        'postgres_fdw',
        -- The substrate itself
        'laplace'
    ) THEN
        RAISE EXCEPTION 'extension % is not in the laplace allowlist', ext_name
            USING HINT = 'Widen the allowlist via bootstrap if substrate-honest; see feedback_conventional_db_reflex.md before adding';
    END IF;
    IF current_database() != 'laplace' THEN
        RAISE EXCEPTION 'laplace_priv.install_extension may only be called from the laplace database (current: %)', current_database();
    END IF;
    EXECUTE format('CREATE EXTENSION IF NOT EXISTS %I', ext_name);
END;
$func$;

REVOKE ALL ON FUNCTION laplace_priv.install_extension(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION laplace_priv.install_extension(text) TO laplace_admin;

CREATE OR REPLACE FUNCTION laplace_priv.drop_extension(ext_name text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_catalog
AS $func$
BEGIN
    IF ext_name NOT IN (
        'postgis', 'postgis_topology', 'postgis_raster', 'postgis_sfcgal',
        'btree_gist', 'btree_gin',
        'pgcrypto',
        'pg_stat_statements', 'auto_explain', 'pg_buffercache',
        'pg_prewarm', 'pg_visibility',
        'postgres_fdw',
        'laplace'
    ) THEN
        RAISE EXCEPTION 'extension % is not in the laplace allowlist', ext_name;
    END IF;
    IF current_database() != 'laplace' THEN
        RAISE EXCEPTION 'laplace_priv.drop_extension may only be called from the laplace database (current: %)', current_database();
    END IF;
    EXECUTE format('DROP EXTENSION IF EXISTS %I CASCADE', ext_name);
END;
$func$;

REVOKE ALL ON FUNCTION laplace_priv.drop_extension(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION laplace_priv.drop_extension(text) TO laplace_admin;
PG_EOF

    # ---------------------------------------------------------------
    # (2b) Pre-create the laplace schema with laplace_admin as owner.
    #      The laplace extension's .control declares `schema = 'laplace'`.
    #      Per PG semantics, if the schema already exists when CREATE
    #      EXTENSION runs, the extension uses it without changing
    #      ownership. By creating it HERE as postgres with explicit
    #      AUTHORIZATION laplace_admin, laplace_admin owns the schema
    #      from the start — subsequent GRANT USAGE / ALTER DEFAULT
    #      PRIVILEGES in DbUp work natively, no privilege workarounds.
    #
    #      If the schema already exists with the WRONG owner (e.g.,
    #      from a prior misconfigured install), bootstrap doesn't try
    #      to "fix" it — that's a clean-slate concern, not bootstrap's
    #      job. Recovery path: `just db-nuke` drops + recreates the
    #      laplace DB; bootstrap re-runs and creates the schema correctly.
    # ---------------------------------------------------------------
    sudo -u postgres psql -d laplace -v ON_ERROR_STOP=1 >/dev/null <<'PG_EOF'
CREATE SCHEMA IF NOT EXISTS laplace AUTHORIZATION laplace_admin;
PG_EOF
    green "✓ Schema 'laplace' present (owner: laplace_admin via fresh creation; recover via 'just db-nuke' if mis-owned from prior install)"
    green "✓ laplace_priv schema + install_extension/drop_extension wrappers"

    # ---------------------------------------------------------------
    # (3) Install postgis directly as postgres (we're already running
    #     `sudo -u postgres psql` — no wrapper needed for THIS install).
    #
    #     Why not via the wrapper? Because at first install, the wrapper
    #     is meant for laplace_admin's RECOVERY path (post-db-nuke).
    #     Direct CREATE EXTENSION as postgres is cleaner: extension's
    #     install script gets postgres's full default-search_path context
    #     and creates objects in public as designed. The DbUp migration's
    #     `SELECT laplace_priv.install_extension('postgis')` will then be
    #     a NOTICE-and-skip no-op (PG short-circuits IF NOT EXISTS BEFORE
    #     the privilege check when the extension is already present).
    # ---------------------------------------------------------------
    if sudo -u postgres psql -d laplace -tAc "SELECT 1 FROM pg_extension WHERE extname='postgis'" | grep -q 1; then
        green "✓ Extension 'postgis' already present in 'laplace'"
    else
        sudo -u postgres psql -d laplace -v ON_ERROR_STOP=1 \
            -c "CREATE EXTENSION postgis" >/dev/null
        green "✓ Installed postgis (direct as postgres; future re-installs go through laplace_priv wrapper)"
    fi

    # ---------------------------------------------------------------
    # (4) Database-level CONNECT grants for app + readonly roles.
    #     (Schema USAGE is Layer 1's job — the schema is created by the
    #     laplace extension's .sql file, not by us here.)
    # ---------------------------------------------------------------
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
    green "✓ CONNECT grants for laplace_app / laplace_readonly"
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

bootstrap_sudoers() {
    say "Sudoers: bounded NOPASSWD for 'make install*'"
    cat > "$SUDOERS_FILE" <<EOF
# Laplace CI runner — bounded NOPASSWD sudo for installing the laplace PG extension.
# The runner user (laplace-runner) needs root only to write to:
#   /usr/lib/postgresql/18/lib/ (extension .so)
#   /usr/share/postgresql/18/extension/ (control + SQL files)
# PGXS's "make install" handles both.

laplace-runner ALL=(root) NOPASSWD: /usr/bin/make install*, /usr/bin/make USE_PGXS=1 *install*
EOF
    chmod 440 "$SUDOERS_FILE"
    chown root:root "$SUDOERS_FILE"

    if visudo -c -f "$SUDOERS_FILE" >/dev/null 2>&1; then
        green "✓ $SUDOERS_FILE valid"
    else
        red "✗ $SUDOERS_FILE failed validation"
        exit 1
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
    bootstrap_legacy_runner_teardown
    bootstrap_runner_install
    bootstrap_runner_register
    bootstrap_runner_service
    bootstrap_pg_roles
    bootstrap_pg_legacy_cleanup
    bootstrap_pg_auth
    bootstrap_pg_database_and_postgis
    bootstrap_sudoers

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
    echo "Sudoers entry (laplace-runner NOPASSWD for /usr/bin/make install*):"
    # Use sudo -ln (list allowed commands, non-interactive) so we don't have
    # to invoke a command that matches the allowed pattern. sudo -n alone
    # would attempt to authorize a specific command and fail under pipefail
    # if the command doesn't match the sudoers pattern — that's a probe bug,
    # not a real failure.
    if sudo -u "$RUNNER_USER" -H sudo -ln 2>&1 | grep -qE 'NOPASSWD.*make install'; then
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
