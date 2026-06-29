#!/bin/bash

set -eo pipefail

RUNNER_USER="laplace-runner"
RUNNER_GROUP="laplace-runner"
RUNNER_HOME="/var/lib/agents/laplace-runner"
RUNNER_HOME_LEGACY="/var/lib/laplace-runner"
RUNNER_DIR="$RUNNER_HOME/actions-runner"
PG_VERSION="18"
LAPLACE_PG_PREFIX="/opt/laplace/pgsql-18"
LAPLACE_PG_DATA="$LAPLACE_PG_PREFIX/data"
LAPLACE_PG_PORT="5432"
LAPLACE_PG_SOCKET_DIR="/var/run/postgresql"
LAPLACE_PG_SERVICE="laplace-postgresql.service"
LAPLACE_PG_LAN_CIDR="${LAPLACE_PG_LAN_CIDR:-192.168.1.0/24}"
PG_CONFIG_DIR="/etc/postgresql/$PG_VERSION/main"
REPO="SaltyPatron/Laplace"
REPO_URL="https://github.com/$REPO"
RUNNER_VERSION="v2.334.0"
RUNNER_TARBALL="actions-runner-linux-x64-${RUNNER_VERSION#v}.tar.gz"
RUNNER_DL_URL="https://github.com/actions/runner/releases/download/$RUNNER_VERSION/$RUNNER_TARBALL"
SUDOERS_FILE="/etc/sudoers.d/laplace-runner"
LAPLACE_PG_CONF_DIR="$LAPLACE_PG_PREFIX/conf"
PG_HBA_FILE="$LAPLACE_PG_CONF_DIR/pg_hba.conf"
PG_IDENT_FILE="$LAPLACE_PG_CONF_DIR/pg_ident.conf"
PG_POSTGRESQL_CONF="$LAPLACE_PG_DATA/postgresql.conf"
RUNNER_SERVICE="actions.runner.SaltyPatron-Laplace.hart-server.service"

GH_SUDO_USER="${SUDO_USER:-ahart}"
MODE="${1:-bootstrap}"
LAPLACE_STRIPE_SUCCESS_URL_DEFAULT="${LAPLACE_STRIPE_SUCCESS_URL:-http://127.0.0.1:5187/billing/success}"
LAPLACE_STRIPE_CANCEL_URL_DEFAULT="${LAPLACE_STRIPE_CANCEL_URL:-http://127.0.0.1:5187/billing/cancel}"
LAPLACE_BILLING_CURRENCY_DEFAULT="${LAPLACE_BILLING_CURRENCY:-usd}"

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

    stripe      Write Stripe sandbox env block into runner .env
                            - Writes LAPLACE_STRIPE_SUCCESS_URL / LAPLACE_STRIPE_CANCEL_URL /
                                LAPLACE_BILLING_CURRENCY
                            - Writes LAPLACE_STRIPE_API_KEY only if exported when invoking this script
                                (example: sudo LAPLACE_STRIPE_API_KEY=sk_test_xxx $0 stripe)
                            - Safe to re-run (managed begin/end block)
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
    local rel_type="$1"
    sudo -u "$GH_SUDO_USER" -H gh api -X POST \
        "repos/$REPO/actions/runners/$rel_type" --jq '.token' 2>/dev/null || true
}

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

    mkdir -p /opt/laplace
    chown "$RUNNER_USER:$RUNNER_GROUP" /opt/laplace
    chmod 2775 /opt/laplace
    green "✓ /opt/laplace: $RUNNER_USER:$RUNNER_GROUP mode 2775 (setgid, no sticky)"
}

bootstrap_migrate_runner_home() {
    say "Migrate runner home: $RUNNER_HOME_LEGACY -> $RUNNER_HOME (dedicated lv-agents LV)"

    if [ ! -d "$RUNNER_HOME_LEGACY" ] || [ -z "$(ls -A "$RUNNER_HOME_LEGACY" 2>/dev/null)" ]; then
        green "✓ No legacy $RUNNER_HOME_LEGACY content to migrate"
        mkdir -p "$RUNNER_HOME"
        chown -R "$RUNNER_USER:$RUNNER_GROUP" "$RUNNER_HOME"
        chmod 750 "$RUNNER_HOME"
        return
    fi

    if [ ! -d /var/lib/agents ] || ! mountpoint -q /var/lib/agents; then
        yellow "  /var/lib/agents not mounted — skipping migration (keeping runner at $RUNNER_HOME_LEGACY)"
        RUNNER_HOME="$RUNNER_HOME_LEGACY"
        RUNNER_DIR="$RUNNER_HOME/actions-runner"
        return
    fi

    local runner_was_active=0
    if systemctl is-active --quiet "$RUNNER_SERVICE" 2>/dev/null; then
        runner_was_active=1
        systemctl stop "$RUNNER_SERVICE"
        echo "  stopped $RUNNER_SERVICE for migration"
    fi

    mkdir -p "$RUNNER_HOME"
    rsync -aHAX "$RUNNER_HOME_LEGACY/" "$RUNNER_HOME/"
    chown -R "$RUNNER_USER:$RUNNER_GROUP" "$RUNNER_HOME"
    chmod 750 "$RUNNER_HOME"
    green "✓ Copied $RUNNER_HOME_LEGACY -> $RUNNER_HOME"

    if [ "$(getent passwd "$RUNNER_USER" | cut -d: -f6)" != "$RUNNER_HOME" ]; then
        usermod -d "$RUNNER_HOME" "$RUNNER_USER"
        green "✓ usermod -d $RUNNER_HOME $RUNNER_USER"
    fi

    local unit_file="/etc/systemd/system/$RUNNER_SERVICE"
    if [ -f "$unit_file" ] && grep -q "$RUNNER_HOME_LEGACY" "$unit_file"; then
        sed -i "s|$RUNNER_HOME_LEGACY|$RUNNER_HOME|g" "$unit_file"
        systemctl daemon-reload
        green "✓ Rewrote $unit_file: $RUNNER_HOME_LEGACY -> $RUNNER_HOME"
    fi

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

bootstrap_runner_oom_guard() {
    say "Runner OOM guard: OOMScoreAdjust drop-in for $RUNNER_SERVICE"
    local dropin_dir="/etc/systemd/system/$RUNNER_SERVICE.d"
    mkdir -p "$dropin_dir"
    cat > "$dropin_dir/10-oom-guard.conf" <<'EOF'
[Service]
OOMScoreAdjust=-800
Restart=always
RestartSec=10
EOF
    systemctl daemon-reload
    green "✓ OOM guard + auto-restart drop-in installed"
}

bootstrap_runner_oom_guard


bootstrap_runner_job_env() {
    grep -q '^LAPLACE_TEST_DB=' "$RUNNER_DIR/.env" 2>/dev/null || echo 'LAPLACE_TEST_DB=laplace' >> "$RUNNER_DIR/.env"
    say "Runner job env: oneAPI machine-static vars → $RUNNER_DIR/.env"
    if [ ! -d "$RUNNER_DIR" ] || [ ! -d /opt/intel/oneapi ]; then
        yellow "runner dir or oneAPI missing — skipping .env block"
        return 0
    fi
    local marker_begin="# >>> laplace-runner managed: oneapi job env >>>"
    local marker_end="# <<< laplace-runner managed: oneapi job env <<<"
    local envfile="$RUNNER_DIR/.env"
    touch "$envfile"
    sed -i -e "/$marker_begin/,/$marker_end/d" "$envfile"
    {
        echo "$marker_begin"
        env -i bash -c 'source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1; printenv' \
          | grep -E '^(ONEAPI_ROOT|CMPLR_ROOT|MKLROOT|TBBROOT|CPATH|LIBRARY_PATH|PKG_CONFIG_PATH|LD_LIBRARY_PATH)='
        echo "$marker_end"
    } >> "$envfile"
    chown "$RUNNER_USER:$RUNNER_GROUP" "$envfile"
    systemctl restart "$RUNNER_SERVICE" 2>/dev/null || true
    green "✓ runner .env oneAPI block written (service restarted to load it)"
}

bootstrap_runner_model_env() {
    say "Runner job env: model snapshot dirs → $RUNNER_DIR/.env"
    if [ ! -d "$RUNNER_DIR" ]; then
        yellow "runner dir missing — skipping model dirs .env block"
        return 0
    fi
    newest_weighted_snapshot() {
        local fam snap
        for fam in /vault/models/$1; do
            [ -d "$fam/snapshots" ] || continue
            for snap in $(ls -t "$fam/snapshots" 2>/dev/null); do
                if ls "$fam/snapshots/$snap"/*.safetensors >/dev/null 2>&1; then
                    echo "$fam/snapshots/$snap"; return 0
                fi
            done
        done
        return 1
    }
    local tiny phi
    tiny="$(newest_weighted_snapshot 'models--TinyLlama--TinyLlama-1.1B-Chat-v1.0' || true)"
    phi="$(newest_weighted_snapshot 'models--microsoft--phi-2' || true)"
    [ -n "$tiny" ] || yellow "  no TinyLlama snapshot with weights under /vault/models — LAPLACE_TINYLLAMA_DIR not written"
    [ -n "$phi" ]  || yellow "  no phi-2 snapshot with weights under /vault/models — LAPLACE_PHI2_DIR not written"
    local marker_begin="# >>> laplace-runner managed: model dirs job env >>>"
    local marker_end="# <<< laplace-runner managed: model dirs job env <<<"
    local envfile="$RUNNER_DIR/.env"
    touch "$envfile"
    sed -i -e "/$marker_begin/,/$marker_end/d" "$envfile"
    {
        echo "$marker_begin"
        [ -n "$tiny" ] && echo "LAPLACE_TINYLLAMA_DIR=$tiny"
        [ -n "$phi" ]  && echo "LAPLACE_PHI2_DIR=$phi"
        echo "$marker_end"
    } >> "$envfile"
    chown "$RUNNER_USER:$RUNNER_GROUP" "$envfile"
    systemctl restart "$RUNNER_SERVICE" 2>/dev/null || true
    green "✓ runner .env model dirs block written (service restarted to load it)"
}

bootstrap_runner_stripe_env() {
    say "Runner job env: Stripe sandbox vars → $RUNNER_DIR/.env"
    if [ ! -d "$RUNNER_DIR" ]; then
        yellow "runner dir missing — skipping Stripe .env block"
        return 0
    fi

    local marker_begin="# >>> laplace-runner managed: stripe sandbox env >>>"
    local marker_end="# <<< laplace-runner managed: stripe sandbox env <<<"
    local envfile="$RUNNER_DIR/.env"
    touch "$envfile"
    sed -i -e "/$marker_begin/,/$marker_end/d" "$envfile"
    {
        echo "$marker_begin"
        echo "LAPLACE_STRIPE_SUCCESS_URL=$LAPLACE_STRIPE_SUCCESS_URL_DEFAULT"
        echo "LAPLACE_STRIPE_CANCEL_URL=$LAPLACE_STRIPE_CANCEL_URL_DEFAULT"
        echo "LAPLACE_BILLING_CURRENCY=$LAPLACE_BILLING_CURRENCY_DEFAULT"
        if [ -n "${LAPLACE_STRIPE_API_KEY:-}" ]; then
            echo "LAPLACE_STRIPE_API_KEY=${LAPLACE_STRIPE_API_KEY}"
        fi
        echo "$marker_end"
    } >> "$envfile"
    chown "$RUNNER_USER:$RUNNER_GROUP" "$envfile"
    systemctl restart "$RUNNER_SERVICE" 2>/dev/null || true
    if [ -n "${LAPLACE_STRIPE_API_KEY:-}" ]; then
        green "✓ runner .env Stripe sandbox block written with API key (service restarted)"
    else
        yellow "✓ runner .env Stripe sandbox block written without API key; set LAPLACE_STRIPE_API_KEY when ready"
    fi
}

bootstrap_runner_service() {
    say "Install + start systemd service ($RUNNER_USER)"
    local unit_file="/etc/systemd/system/$RUNNER_SERVICE"
    if [ -f "$unit_file" ]; then
        green "✓ Service unit $unit_file already exists — skipping svc.sh install"
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

bootstrap_disable_system_postgresql() {
    say "Disable system postgresql@$PG_VERSION-main (substrate uses /opt/laplace cluster only)"
    local sys_unit="postgresql@${PG_VERSION}-main.service"
    local sys_wrapper="postgresql.service"

    local sys_status=0
    systemctl status "$sys_unit" --no-pager >/dev/null 2>&1 || sys_status=$?
    if [ $sys_status -eq 4 ]; then
        green "✓ System $sys_unit not present — nothing to disable"
        return
    fi

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

    if systemctl is-enabled --quiet "$sys_wrapper" 2>/dev/null; then
        systemctl mask "$sys_wrapper" >/dev/null 2>&1 || true
        green "✓ Masked $sys_wrapper wrapper (prevents boot-time auto-start)"
    fi
}

bootstrap_laplace_pg_cluster() {
    say "Provision /opt/laplace/pgsql-18 cluster (substrate runtime PG)"

    if [ ! -x "$LAPLACE_PG_PREFIX/bin/postgres" ]; then
        red "✗ $LAPLACE_PG_PREFIX/bin/postgres missing — run \`just build-deps\` first"
        return 1
    fi

    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$LAPLACE_PG_PREFIX/log"
    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$LAPLACE_PG_CONF_DIR"

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

    chown -R "$RUNNER_USER:$RUNNER_GROUP" "$LAPLACE_PG_DATA"
    chmod 0700 "$LAPLACE_PG_DATA"
    green "✓ $LAPLACE_PG_DATA owned by $RUNNER_USER (mode 0700)"

    local marker_begin="# >>> laplace-runner managed: cluster config >>>"
    local marker_end="# <<< laplace-runner managed: cluster config <<<"
    local pg_listen="localhost"
    [ -n "$LAPLACE_PG_LAN_CIDR" ] && pg_listen="*"
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
port = $LAPLACE_PG_PORT
listen_addresses = '$pg_listen'
unix_socket_directories = '$LAPLACE_PG_SOCKET_DIR,/tmp'
extension_control_path = '/opt/laplace/share/postgresql/$PG_VERSION:\$system'
dynamic_library_path = '\$libdir:/opt/laplace/lib/postgresql/$PG_VERSION'
logging_collector = on
log_directory = '$LAPLACE_PG_PREFIX/log'
log_filename = 'postgresql-%Y-%m-%d_%H%M%S.log'
log_file_mode = 0640
hba_file = '$PG_HBA_FILE'
ident_file = '$PG_IDENT_FILE'

shared_buffers = 32GB
effective_cache_size = 96GB
maintenance_work_mem = 8GB
work_mem = 256MB
wal_compression = on
max_wal_size = 32GB
min_wal_size = 2GB
checkpoint_timeout = 30min
checkpoint_completion_target = 0.9
wal_buffers = 64MB
synchronous_commit = off
random_page_cost = 1.1
effective_io_concurrency = 200
maintenance_io_concurrency = 200
default_statistics_target = 200
max_worker_processes = 8
max_parallel_workers = 6
max_parallel_maintenance_workers = 4
max_parallel_workers_per_gather = 4
huge_pages = try
$marker_end
EOF
    green "✓ Wrote substrate cluster config to $PG_POSTGRESQL_CONF"

    sudo -u "$RUNNER_USER" tee "$PG_HBA_FILE" >/dev/null <<EOF
local   all             laplace_admin                           peer map=laplace_map
local   all             all                                     peer
host    all             all             127.0.0.1/32            trust
host    all             all             ::1/128                 trust
EOF
    if [ -n "$LAPLACE_PG_LAN_CIDR" ]; then
        printf 'host    all             all             %-22s trust\n' "$LAPLACE_PG_LAN_CIDR" \
            | sudo -u "$RUNNER_USER" tee -a "$PG_HBA_FILE" >/dev/null
        green "✓ pg_hba: remote access enabled for $LAPLACE_PG_LAN_CIDR (trust); listen_addresses=$pg_listen"
    fi
    sudo -u "$RUNNER_USER" chmod 0600 "$PG_HBA_FILE"
    green "✓ Wrote substrate cluster pg_hba.conf (managed block as full file)"

    if ! sudo -u "$RUNNER_USER" grep -q "^laplace_map" "$PG_IDENT_FILE" 2>/dev/null; then
        sudo -u "$RUNNER_USER" tee -a "$PG_IDENT_FILE" >/dev/null <<EOF

laplace_map   laplace-runner   laplace_admin
laplace_map   ahart            laplace_admin
laplace_map   postgres         laplace_admin
EOF
        green "✓ Wrote laplace_map to $PG_IDENT_FILE"
    else
        green "✓ laplace_map already present in $PG_IDENT_FILE"
    fi

    install -d -m 2775 -o postgres -g "$RUNNER_GROUP" "$LAPLACE_PG_SOCKET_DIR" 2>/dev/null \
        || install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "$LAPLACE_PG_SOCKET_DIR"
    green "✓ $LAPLACE_PG_SOCKET_DIR writable by $RUNNER_GROUP"

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
ExecStartPre=+/usr/bin/install -d -m 2775 -o postgres -g $RUNNER_GROUP $LAPLACE_PG_SOCKET_DIR
ExecStart=$LAPLACE_PG_PREFIX/bin/postgres -D $LAPLACE_PG_DATA
ExecReload=/bin/kill -HUP \$MAINPID
KillMode=mixed
KillSignal=SIGINT
TimeoutSec=120
Restart=on-failure
UMask=0027

[Install]
WantedBy=multi-user.target
EOF
    chmod 644 "$unit_file"
    systemctl daemon-reload
    green "✓ Installed $unit_file"

    systemctl enable "$LAPLACE_PG_SERVICE" >/dev/null 2>&1 || true
    systemctl reset-failed "$LAPLACE_PG_SERVICE" 2>/dev/null || true
    if systemctl is-active --quiet "$LAPLACE_PG_SERVICE" 2>/dev/null; then
        systemctl restart "$LAPLACE_PG_SERVICE"
        green "✓ Restarted $LAPLACE_PG_SERVICE (picks up new conf + ld.so.cache)"
    else
        systemctl start "$LAPLACE_PG_SERVICE"
        green "✓ Enabled + started $LAPLACE_PG_SERVICE"
    fi

    local tries=0
    while [ $tries -lt 30 ]; do
        if [ -S "$LAPLACE_PG_SOCKET_DIR/.s.PGSQL.$LAPLACE_PG_PORT" ]; then
            green "✓ Substrate cluster accepting connections on $LAPLACE_PG_SOCKET_DIR/.s.PGSQL.$LAPLACE_PG_PORT"
            validate_pg_tuning
            return $?
        fi
        sleep 1
        tries=$((tries + 1))
    done
    red "✗ Substrate cluster failed to come up — check journalctl -u $LAPLACE_PG_SERVICE"
    return 1
}

validate_pg_tuning() {
    say "Validate cluster tuning is live (post-restart)"
    local vbad=0 nm live ok pend
    while IFS='|' read -r nm live ok pend; do
        [ -z "$nm" ] && continue
        if [ "$ok" != "t" ]; then
            red "  ✗ $nm = '$live' (not the expected tuned value)"; vbad=1
        elif [ "$pend" = "t" ]; then
            red "  ✗ $nm pending_restart — the cluster was not fully restarted"; vbad=1
        else
            green "  ✓ $nm = $live"
        fi
    done < <(sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/psql" \
        -h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" -d postgres -U laplace_admin -tAF'|' 2>/dev/null <<'PG_EOF'
WITH want(name, expected, mode) AS (VALUES
  ('shared_buffers','32GB','mem'), ('effective_cache_size','96GB','mem'),
  ('maintenance_work_mem','8GB','mem'), ('work_mem','256MB','mem'),
  ('max_wal_size','32GB','mem'), ('min_wal_size','2GB','mem'), ('wal_buffers','64MB','mem'),
  ('synchronous_commit','off','eq'), ('checkpoint_timeout','30min','eq'),
  ('wal_compression','on','enabled'), ('max_parallel_maintenance_workers','4','eq'),
  ('effective_io_concurrency','200','eq'), ('huge_pages','try','eq'))
SELECT w.name, current_setting(w.name),
       CASE w.mode
         WHEN 'mem'     THEN pg_size_bytes(current_setting(w.name)) = pg_size_bytes(w.expected)
         WHEN 'enabled' THEN current_setting(w.name) <> 'off'
         ELSE current_setting(w.name) = w.expected END,
       s.pending_restart
FROM want w JOIN pg_settings s ON s.name = w.name
ORDER BY w.name;
PG_EOF
)
    local npend
    npend=$(sudo -u "$RUNNER_USER" "$LAPLACE_PG_PREFIX/bin/psql" \
        -h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" -d postgres -U laplace_admin -tAc \
        "SELECT count(*) FROM pg_settings WHERE pending_restart" 2>/dev/null)
    if [ "${npend:-1}" != "0" ]; then
        red "  ✗ ${npend:-?} setting(s) pending_restart — cluster not fully restarted"; vbad=1
    fi
    if [ "$vbad" -ne 0 ]; then
        red "✗ Tuning NOT fully live — likely a stale postgresql.auto.conf override or no restart."
        red "   Inspect: psql -d postgres -U laplace_admin -c \"SELECT name,setting,source,pending_restart FROM pg_settings WHERE source NOT IN ('default','override')\""
        return 1
    fi
    green "✓ Cluster tuning validated live (expected values applied; nothing pending_restart)"
    return 0
}

bootstrap_pg_roles() {
    say "Ensure PG roles: laplace_admin (SUPERUSER) / laplace_app / laplace_readonly"
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

    local pgbin="$LAPLACE_PG_PREFIX/bin"
    local conn=(-h "$LAPLACE_PG_SOCKET_DIR" -p "$LAPLACE_PG_PORT" -U laplace_admin)

    if sudo -u "$RUNNER_USER" "$pgbin/psql" "${conn[@]}" -d postgres -tAc \
        "SELECT 1 FROM pg_database WHERE datname='laplace'" | grep -q 1; then
        green "✓ Database 'laplace' already exists (use 'just db-nuke' for a clean slate)"
    else
        sudo -u "$RUNNER_USER" "$pgbin/createdb" "${conn[@]}" -O laplace_admin laplace
        green "✓ Created database 'laplace' owned by laplace_admin"
    fi

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

bootstrap_pg_auth() {
    green "✓ pg_hba.conf + pg_ident.conf maintained by bootstrap_laplace_pg_cluster"
}

bootstrap_engine_lib_path() {
    say "Register /opt/laplace/lib + dep prefixes + Intel oneAPI runtime with ld.so.conf.d"
    local conf=/etc/ld.so.conf.d/laplace.conf
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

bootstrap_pg_extension_paths() {
    green "✓ extension_control_path + dynamic_library_path set by bootstrap_laplace_pg_cluster"
}

bootstrap_cleanup_stale_installs() {
    say "Remove stale Laplace + Hartonomous installs from system PG + /usr/local"
    local removed=0
    local f

    for f in /usr/local/lib/liblaplace_*.so*; do
        [ -e "$f" ] || continue
        rm -f "$f" && removed=$((removed + 1))
    done

    for f in /usr/lib/postgresql/$PG_VERSION/lib/laplace.so \
             /usr/lib/postgresql/$PG_VERSION/lib/laplace_geom.so \
             /usr/lib/postgresql/$PG_VERSION/lib/laplace_substrate.so; do
        [ -e "$f" ] || continue
        rm -f "$f" && removed=$((removed + 1))
    done

    for f in /usr/share/postgresql/$PG_VERSION/extension/laplace.control \
             /usr/share/postgresql/$PG_VERSION/extension/laplace--*.sql \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_geom.control \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_geom--*.sql \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_substrate.control \
             /usr/share/postgresql/$PG_VERSION/extension/laplace_substrate--*.sql; do
        [ -e "$f" ] || continue
        rm -f "$f" && removed=$((removed + 1))
    done

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
    say "Remove legacy /etc/sudoers.d/laplace-runner (workaround no longer needed)"
    if [ -f "$SUDOERS_FILE" ]; then
        rm -f "$SUDOERS_FILE"
        green "✓ Removed $SUDOERS_FILE (cmake install no longer needs sudo)"
    else
        green "✓ $SUDOERS_FILE already absent"
    fi
}

bootstrap_external_dirs() {
    say "Ensure /opt/laplace/external/ + per-dep install destinations + engine install destinations"
    install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" /opt/laplace/external
    for dep in tree-sitter geos proj gdal pgsql-18; do
        install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "/opt/laplace/$dep"
    done
    for sub in include lib share bin; do
        install -d -m 2775 -o "$RUNNER_USER" -g "$RUNNER_GROUP" "/opt/laplace/$sub"
    done
    green "✓ /opt/laplace/{external,tree-sitter,geos,proj,gdal,pgsql-18,include,lib,share,bin}/ ready (owned $RUNNER_USER:$RUNNER_GROUP, mode 2775 setgid)"
    echo "  → /opt/laplace/external/ populated by: scripts/sync-external.sh (pipeline step or developer)"
}

bootstrap_runner_gh_auth() {
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

    if sudo -u "$RUNNER_USER" -H gh auth status >/dev/null 2>&1; then
        green "✓ $RUNNER_USER can authenticate with GitHub"
    else
        yellow "  gh auth status failed under $RUNNER_USER — token may have host-binding or scope issue"
    fi

    if sudo -u "$RUNNER_USER" -H gh auth setup-git >/dev/null 2>&1; then
        green "✓ git credential helper wired to gh for $RUNNER_USER"
    else
        yellow "  gh auth setup-git failed under $RUNNER_USER — submodule clones will fall back to anonymous"
    fi
}

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

    say "Stripe sandbox env block (runner .env)"
    if [ -f "$RUNNER_DIR/.env" ] && grep -q "laplace-runner managed: stripe sandbox env" "$RUNNER_DIR/.env"; then
        grep -E '^(LAPLACE_STRIPE_SUCCESS_URL|LAPLACE_STRIPE_CANCEL_URL|LAPLACE_BILLING_CURRENCY)=' "$RUNNER_DIR/.env" \
            | sed 's/^/  /'
        if grep -q '^LAPLACE_STRIPE_API_KEY=' "$RUNNER_DIR/.env"; then
            echo "  LAPLACE_STRIPE_API_KEY=***set***"
        else
            echo "  LAPLACE_STRIPE_API_KEY=(not set)"
        fi
    else
        echo "  (no managed Stripe sandbox block in $RUNNER_DIR/.env)"
    fi
}

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

    say "Remove sudoers"
    rm -f "$SUDOERS_FILE"
    green "✓ $SUDOERS_FILE removed"

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

    say "Remove runner installation"
    if [ -d "$RUNNER_DIR" ]; then
        rm -rf "$RUNNER_DIR"
        green "✓ Removed $RUNNER_DIR"
    fi
    if [ -d "$RUNNER_HOME" ]; then
        rm -rf "$RUNNER_HOME"
        green "✓ Removed $RUNNER_HOME"
    fi
    if [ -d "$RUNNER_HOME_LEGACY" ] && [ "$RUNNER_HOME_LEGACY" != "$RUNNER_HOME" ]; then
        rm -rf "$RUNNER_HOME_LEGACY"
        green "✓ Removed legacy $RUNNER_HOME_LEGACY"
    fi
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

do_bootstrap() {
    bootstrap_user
    bootstrap_build_environment
    bootstrap_migrate_runner_home
    bootstrap_legacy_runner_teardown
    bootstrap_runner_install
    bootstrap_runner_register
    bootstrap_runner_service
    bootstrap_runner_job_env
    bootstrap_runner_model_env
    bootstrap_runner_stripe_env

    bootstrap_engine_lib_path
    bootstrap_external_dirs

    bootstrap_disable_system_postgresql
    bootstrap_laplace_pg_cluster

    bootstrap_pg_roles
    bootstrap_pg_legacy_cleanup
    bootstrap_pg_auth
    bootstrap_pg_database_and_postgis

    bootstrap_cleanup_stale_installs
    bootstrap_pg_extension_paths
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
    if sudo -u "$RUNNER_USER" -H sudo -ln 2>&1 | grep -qE 'NOPASSWD.*(cmake --install|make install)'; then
        red "✗ Legacy sudoers rule still active for laplace-runner (should be removed; see step 10)"
    else
        green "✓ No legacy sudoers rule (staged /opt/laplace install needs none)"
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
    echo "  gh workflow run laplace.yml"
    echo
    echo "Layer 1 reset (start fresh without touching Layer 0):"
    echo "  just db-nuke       # DROP DATABASE laplace, re-create empty"
    echo
    echo "Full reset (undo Layer 0):"
    echo "  sudo $0 reset"
}

do_stripe() {
    require_root
    bootstrap_runner_stripe_env
    green "===== STRIPE SANDBOX ENV BOOTSTRAP COMPLETE ====="
    echo "Tip: to write key non-interactively:"
    echo "  sudo LAPLACE_STRIPE_API_KEY=sk_test_xxx $0 stripe"
}

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
