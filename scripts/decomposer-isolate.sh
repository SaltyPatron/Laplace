#!/usr/bin/env bash
# DROP + CREATE an isolated Laplace database with extensions (Linux/CI).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DBNAME="${1:-}"
RECYCLE=0

if [[ "${1:-}" == "--recycle" ]]; then
  RECYCLE=1
  DBNAME="${2:-}"
fi

if [[ -z "$DBNAME" ]]; then
  echo "usage: $0 <dbname> [--recycle]" >&2
  exit 2
fi

PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1)

"${PSQL[@]}" -d postgres -c \
  "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='${DBNAME}' AND pid<>pg_backend_pid();"

"${PSQL[@]}" -d postgres -c "DROP DATABASE IF EXISTS \"${DBNAME}\";"
createdb -h "$PGHOST" -U "$PGUSER" -O "$PGUSER" "$DBNAME"

"${PSQL[@]}" -d "$DBNAME" \
  -c "CREATE EXTENSION IF NOT EXISTS postgis;" \
  -c "CREATE EXTENSION IF NOT EXISTS laplace_geom;" \
  -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate;"

"${PSQL[@]}" -d "$DBNAME" -P pager=off \
  -c "SET search_path = laplace, public; SELECT * FROM substrate_health();"

if [[ "$RECYCLE" == "1" ]]; then
  echo "WARN: --recycle not implemented on Linux (install extensions manually if needed)"
fi

echo "DB-ISOLATE COMPLETE: $DBNAME"
