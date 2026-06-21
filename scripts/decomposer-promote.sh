#!/usr/bin/env bash
# Promote a validated decomposer: re-run ingest on canonical laplace (idempotent, not pg clone).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"
SOURCE="${1:-}"

[[ -n "$SOURCE" ]] || { echo "usage: $0 <source>" >&2; exit 2; }

export LAPLACE_CANONICAL_DB="${LAPLACE_CANONICAL_DB:-laplace}"
REPORT="$ROOT/.ingest-proof/decomposer-${SOURCE}.json"
MARKER_DIR="$ROOT/.ingest-proof/promoted"
mkdir -p "$MARKER_DIR"

if [[ -f "$REPORT" ]]; then
  python3 -c "import json,sys; r=json.load(open('$REPORT')); sys.exit(0 if r.get('passed') else 1)" \
    || { echo "gate report failed: $REPORT" >&2; exit 1; }
else
  echo "WARN: no gate report at $REPORT — proceeding"
fi

export LAPLACE_DBNAME="$LAPLACE_CANONICAL_DB"
export LAPLACE_DB="Host=${PGHOST:-/var/run/postgresql};Username=${PGUSER:-laplace_admin};Database=${LAPLACE_CANONICAL_DB}"

echo "==== promote $SOURCE into $LAPLACE_CANONICAL_DB ===="

if [[ "$SOURCE" == "document" ]]; then
  doc="${LAPLACE_DATA_ROOT:-/vault/Data}/test-data/text"
  [[ -d "$doc" ]] || { echo "document promote requires $doc" >&2; exit 1; }
  "$SCRIPTS/ingest-source.sh" document "$doc"
else
  "$SCRIPTS/ingest-source.sh" "$SOURCE"
fi

date -u +"%Y-%m-%dT%H:%M:%SZ promoted" > "$MARKER_DIR/${SOURCE}.marker"
echo "db=$LAPLACE_CANONICAL_DB" >> "$MARKER_DIR/${SOURCE}.marker"

echo "DECOMPOSER-PROMOTE OK: $SOURCE -> $LAPLACE_CANONICAL_DB"
