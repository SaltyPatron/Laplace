#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"

PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-laplace}"

PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1 -tAc)

layer_ok() {
  local decomposer="$1" layer="$2"
  "${PSQL[@]}" -d "$DB" \
    "SELECT laplace.evidence_count(p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/${layer}/v1'), p_source => laplace.source_id('${decomposer}')) > 0;" \
    | grep -qi true
}

if layer_ok UnicodeDecomposer 0 && layer_ok ISO639Decomposer 1; then
  echo "floor layers OK on $DB"
  exit 0
fi

echo "==== ensure-floor: unicode + iso639 on $DB ===="
export LAPLACE_DBNAME="$DB"
export LAPLACE_DB="Host=${PGHOST};Username=${PGUSER};Database=${DB}"

"$SCRIPTS/ingest-source.sh" unicode
"$SCRIPTS/ingest-source.sh" iso639

echo "ENSURE-FLOOR COMPLETE: $DB"
