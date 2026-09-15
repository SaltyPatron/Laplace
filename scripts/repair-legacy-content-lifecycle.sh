#!/usr/bin/env bash
# Hold the existing ingest exclusion lane on exactly the database psql repairs.
# Managed shutdown/restoration belongs to product-ci's existing service controls.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
database="${1:?database name required}"
host="${PGHOST:-/var/run/postgresql}"
port="${PGPORT:-5432}"
role="${PGUSER:-laplace_admin}"
# Npgsql accepts double-quoted connection-string values and doubled quotes.
# Bind every psql target coordinate explicitly: inherited LAPLACE_DB otherwise
# outranks PGDATABASE and can put the exclusion lock on a different database.
connection="Host=\"${host//\"/\"\"}\";Port=\"${port//\"/\"\"}\";Username=\"${role//\"/\"\"}\";Database=\"${database//\"/\"\"}\""
LAPLACE_DB="$connection" PGDATABASE="$database" \
  bash scripts/measure-lane.sh -- \
    python3 scripts/repair-legacy-content.py --database "$database" \
    --receipt-root /build/laplace/recovery/legacy-content-repair
