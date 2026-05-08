#!/bin/bash
# laplace-pg entrypoint.
# - First boot: run initdb, create app role + database, run
#   /docker-entrypoint-initdb.d/*.sql.
# - Subsequent boots: just exec postgres.
set -euo pipefail

PGDATA="${PGDATA:-/var/lib/postgresql/data}"
PG_BIN=/opt/pg18/bin

# Standard postgres-image env-var contract: docker-compose passes these in,
# this entrypoint creates the matching role + database on first boot.
APP_USER="${POSTGRES_USER:-postgres}"
APP_PASS="${POSTGRES_PASSWORD:-postgres}"
APP_DB="${POSTGRES_DB:-laplace}"

if [ ! -s "$PGDATA/PG_VERSION" ]; then
    echo "[laplace-pg] Initializing new cluster at $PGDATA"
    "$PG_BIN/initdb" \
        --pgdata="$PGDATA" \
        --username=postgres \
        --encoding=UTF8 \
        --locale=C.UTF-8 \
        --auth-local=trust \
        --auth-host=scram-sha-256 \
        --data-checksums

    # Allow remote connections (Docker network).
    echo "host all all 0.0.0.0/0 scram-sha-256" >> "$PGDATA/pg_hba.conf"
    echo "listen_addresses = '*'" >> "$PGDATA/postgresql.conf"

    # Bootstrap-only settings. Production values come from the docker-compose
    # `command:` section on every container start, so this entrypoint only
    # needs the minimum for first-boot init.
    cat >> "$PGDATA/postgresql.conf" <<EOF
shared_buffers = 1GB
work_mem = 64MB
maintenance_work_mem = 256MB
max_wal_size = 8GB
synchronous_commit = off
# laplace_pg loads lazily via CREATE EXTENSION + per-call dlopen, NOT via
# shared_preload_libraries. Preloading runs the extension's _PG_init() in
# every backend at fork time and corrupts backend memory state even when
# subsequent queries don't call any extension function. Seed phases use
# only stock PG + PostGIS; extension functions load on demand.
EOF

    # Start postgres temporarily to run role/db creation + init scripts.
    "$PG_BIN/pg_ctl" -D "$PGDATA" -o "-c listen_addresses=''" -w start

    # Create app role + database (unless they already exist — postgres user is
    # special-cased so we only create when not == 'postgres').
    if [ "$APP_USER" != "postgres" ]; then
        echo "[laplace-pg] Creating role '$APP_USER'"
        "$PG_BIN/psql" -v ON_ERROR_STOP=1 --username=postgres --dbname=postgres <<SQL
CREATE ROLE "$APP_USER" WITH LOGIN SUPERUSER PASSWORD '$APP_PASS';
SQL
    else
        "$PG_BIN/psql" -v ON_ERROR_STOP=1 --username=postgres --dbname=postgres -c "ALTER ROLE postgres WITH PASSWORD '$APP_PASS';"
    fi

    echo "[laplace-pg] Creating database '$APP_DB' (owner $APP_USER)"
    "$PG_BIN/psql" -v ON_ERROR_STOP=1 --username=postgres --dbname=postgres <<SQL
CREATE DATABASE "$APP_DB" OWNER "$APP_USER";
SQL

    if [ -d /docker-entrypoint-initdb.d ]; then
        for f in /docker-entrypoint-initdb.d/*.sql; do
            [ -e "$f" ] || continue
            echo "[laplace-pg] Running $f as $APP_USER on $APP_DB"
            "$PG_BIN/psql" -v ON_ERROR_STOP=1 --username="$APP_USER" --dbname="$APP_DB" -f "$f"
        done
    fi

    "$PG_BIN/pg_ctl" -D "$PGDATA" -m fast -w stop
    echo "[laplace-pg] Initialization complete."
fi

# CMD is "postgres" (or any pg binary). Resolve via PG_BIN.
cmd="$1"; shift || true
exec "$PG_BIN/$cmd" "$@"
