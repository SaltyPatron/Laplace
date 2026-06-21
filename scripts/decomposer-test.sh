#!/usr/bin/env bash
# Test one decomposer in a fresh isolated DB: prerequisites + target ingest, then gates.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"

SOURCE="${1:-}"
CUSTOM_DB=""
SKIP_GATES=0
shift || true

while [[ $# -gt 0 ]]; do
  case "$1" in
    --db) CUSTOM_DB="$2"; shift 2 ;;
    --skip-gates) SKIP_GATES=1; shift ;;
    *) echo "unknown flag: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$SOURCE" ]] || { echo "usage: $0 <source> [--db <dbname>] [--skip-gates]" >&2; exit 2; }

export LAPLACE_ISOLATE_PREFIX="${LAPLACE_ISOLATE_PREFIX:-laplace_d}"
export LAPLACE_CANONICAL_DB="${LAPLACE_CANONICAL_DB:-laplace}"

eval "$("$SCRIPTS/decomposer-isolate-plan.py" --source "$SOURCE")"
[[ -n "${TARGET_DB:-}" ]] || { echo "could not resolve target DB" >&2; exit 2; }
[[ -n "$CUSTOM_DB" ]] && TARGET_DB="$CUSTOM_DB"

echo "==== decomposer-test: $SOURCE on $TARGET_DB ===="
if [[ -n "${PREREQ_SOURCES:-}" ]]; then
  echo "==== prerequisites: $PREREQ_SOURCES ===="
fi

"$SCRIPTS/decomposer-isolate.sh" "$TARGET_DB"

export LAPLACE_DBNAME="$TARGET_DB"
export LAPLACE_DB="Host=${PGHOST:-/var/run/postgresql};Username=${PGUSER:-laplace_admin};Database=${TARGET_DB}"

for prereq in ${PREREQ_SOURCES:-}; do
  echo "==== prerequisite ingest: $prereq ===="
  if [[ "$prereq" == "document" ]]; then
    doc="${LAPLACE_DATA_ROOT:-/vault/Data}/test-data/text"
    [[ -d "$doc" ]] || { echo "document prerequisite requires $doc" >&2; exit 1; }
    "$SCRIPTS/ingest-source.sh" document "$doc"
  else
    "$SCRIPTS/ingest-source.sh" "$prereq"
  fi
done

echo "==== target ingest: $SOURCE ===="
if [[ "$SOURCE" == "document" ]]; then
  doc="${LAPLACE_DATA_ROOT:-/vault/Data}/test-data/text"
  [[ -d "$doc" ]] || { echo "document test requires $doc" >&2; exit 1; }
  "$SCRIPTS/ingest-source.sh" document "$doc"
else
  "$SCRIPTS/ingest-source.sh" "$SOURCE"
fi

if [[ "$SKIP_GATES" == "0" ]]; then
  python3 "$SCRIPTS/decomposer-gate-check.py" --source "$SOURCE" --dbname "$TARGET_DB" \
    --user "${PGUSER:-laplace_admin}" --host "${PGHOST:-/var/run/postgresql}"
fi

echo "DECOMPOSER-TEST OK: $SOURCE on $TARGET_DB"
