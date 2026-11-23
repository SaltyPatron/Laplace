#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
db="${LAPLACE_DBNAME:-${PGDATABASE:-laplace-mono}}"
bash "$root/scripts/check-database-health.sh" --installed-runtime "$db"
count="$(psql -X -v ON_ERROR_STOP=1 -d "$db" -tAc "SELECT count(*) FROM ops.substrate_counts();")"
[[ "${count:-0}" == "4" ]] || {
  echo "Expected 4 canonical metrics from ops.substrate_counts(), got '${count:-}'" >&2
  exit 1
}
echo "PASS installed canonical 4-table substrate health and ops.substrate_counts() verified"
