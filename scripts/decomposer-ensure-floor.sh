#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"

PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-laplace}"

PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1)

# Layer completion is keyed by the source witness; decomposer-gates.json resolves a
# recipe generation to its witness and a legacy decomposer to its named source.
layer_ok() {
  local predicate
  predicate="$(python3 "$SCRIPTS/source-layer-complete.py" "$@" | cut -f1)" || return 1
  "${PSQL[@]}" -d "$DB" -tAc "SELECT ${predicate};" | grep -qiE '^(t|true)$'
}

if layer_ok unicode UnicodeDecomposer 0 && layer_ok iso639 ISO639Decomposer 1; then
  echo "floor layers OK on $DB"
  exit 0
fi

echo "==== ensure-floor: unicode + iso639 on $DB ===="
export LAPLACE_DBNAME="$DB"
export LAPLACE_DB="Host=${PGHOST};Username=${PGUSER};Database=${DB}"

"$SCRIPTS/ingest-source.sh" unicode
"$SCRIPTS/ingest-source.sh" iso639

echo "ENSURE-FLOOR COMPLETE: $DB"
