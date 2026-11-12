#!/usr/bin/env bash
# A job's own PostgreSQL on this repository's runner: the fixture server a hosted job got from a
# postgres:18 service container. The runner account has no container runtime and no root, and the
# host's own cluster is never a fixture, so the cluster is made from the host's PostgreSQL programs
# under the runner's temp directory, listens on loopback at a free port with the fixture superuser
# `postgres`, and is stopped and removed by `stop` (the runner empties its temp directory besides).
#
#   private-postgres.sh start   make and start the cluster; PGPORT goes to $GITHUB_ENV
#   private-postgres.sh stop    stop it and remove its files
set -euo pipefail
: "${RUNNER_TEMP:?}" "${PGPASSWORD:?Declare the fixture password}"
root="$RUNNER_TEMP/private-postgres"
bin="${LAPLACE_PG_DIR:-/usr/local/pgsql}/bin"

stop() {
  [[ -d "$root/data" ]] || return 0
  "$bin/pg_ctl" -D "$root/data" -m immediate -w -t 60 stop >/dev/null 2>&1 || true
  rm -rf -- "$root"
}

case "${1:-}" in
  start)
    [[ -x "$bin/initdb" && -x "$bin/pg_ctl" ]] || { echo "PostgreSQL programs are missing: $bin" >&2; exit 2; }
    [[ $("$bin/postgres" --version) == *' 18.'* ]] || { echo 'The fixture server must be PostgreSQL 18' >&2; exit 2; }
    [[ ! -e "$root" ]] || { echo "A private cluster already exists: $root" >&2; exit 2; }
    umask 077
    mkdir -p "$root/socket"
    trap 'status=$?; (( status == 0 )) || { cat "$root/log" >&2 2>/dev/null || true; stop; }; exit "$status"' EXIT
    printf '%s\n' "$PGPASSWORD" > "$root/password"
    "$bin/initdb" -D "$root/data" -U postgres --pwfile="$root/password" --auth=scram-sha-256 \
      --encoding=UTF8 --locale=C.UTF-8 >/dev/null
    rm -f -- "$root/password"
    port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')
    "$bin/pg_ctl" -D "$root/data" -l "$root/log" -w -t 60 \
      -o "-c listen_addresses=127.0.0.1 -c port=$port -c unix_socket_directories=$root/socket -c fsync=off" start >/dev/null
    PGHOST=127.0.0.1 PGPORT=$port PGUSER=postgres PGDATABASE=postgres \
      "$bin/psql" -X -Atq -v ON_ERROR_STOP=1 -c 'SELECT version()'
    echo "PGPORT=$port" >> "${GITHUB_ENV:?}"
    echo "Private PostgreSQL: 127.0.0.1:$port ($root)"
    ;;
  stop) stop ;;
  *) echo 'usage: private-postgres.sh start|stop' >&2; exit 2 ;;
esac
