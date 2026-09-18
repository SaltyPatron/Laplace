#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
LAPLACE="$ROOT/scripts/laplace"

usage() {
  echo "Usage: $0 recover|end" >&2
  exit 2
}

recover() {
  "$LAPLACE" recover-indexes
  pending="$(psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -X -tAc \
    "SELECT count(*) FROM laplace.index_cycle_journal;" | xargs)"
  [[ "$pending" == 0 ]] || {
    echo "foundation index recovery left $pending journal row(s) on $DB" >&2
    exit 1
  }
  echo "FOUNDATION_INDEX_RECOVERY database=$DB journaled=0"
}

case "${1:-}" in
  recover|end)
    # Compatibility with old interrupted campaigns: rebuild what they journaled.
    # Starting a new drop/load/rebuild campaign is intentionally impossible because
    # O(tier) ingestion requires these secondaries online while it is running.
    recover
    ;;
  begin)
    echo "foundation index-drop campaigns are retired; O(tier) ingest requires secondaries online" >&2
    exit 2
    ;;
  *) usage ;;
esac
