#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"

PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
FORCE=0
CHECK_ONLY=0
REQUIRED_LEXICAL=0

usage() {
  echo "Usage: $0 [--force|--check-only] [--required-lexical]" >&2
  echo "  --force       always re-ingest foundation sources" >&2
  echo "  --check-only  report incomplete foundation layers without ingest" >&2
  echo "  --required-lexical  admit Unicode, ISO639, CILI and WordNet" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force) FORCE=1; shift ;;
    --check-only) CHECK_ONLY=1; shift ;;
    --required-lexical) REQUIRED_LEXICAL=1; shift ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1)

db_exists() {
  "${PSQL[@]}" -d postgres -tAc \
    "SELECT 1 FROM pg_database WHERE datname='${DB}'" 2>/dev/null | grep -q 1
}

# Layer completion is operational state keyed by the source witness and layer. A
# recipe source generation completes under its witness [authority, release]; a legacy
# decomposer under its named source. decomposer-gates.json resolves both.
layer_predicate() {
  python3 "$SCRIPTS/source-layer-complete.py" "$@"
}

layer_ok() {
  local cli="$1" predicate
  db_exists || return 1
  predicate="$(layer_predicate "$cli" | cut -f1)" || return 1
  "${PSQL[@]}" -d "$DB" -tAc "SELECT ${predicate};" \
    | grep -qiE '^(t|true)$'
}

layer_label() {
  layer_predicate "$@" | cut -f2
}

# The foundation batch and its order are declared once, in scripts/win/witness-manifest.json
# (foundation.sources); each source's witness or decomposer, and its layer, come from
# scripts/decomposer-gates.json through source-layer-complete.py.
mapfile -t FOUNDATION < <(python3 -c 'import json, sys; print("\n".join(json.load(open(sys.argv[1], encoding="utf-8"))["foundation"]["sources"]))' "$SCRIPTS/win/witness-manifest.json")
[[ ${#FOUNDATION[@]} -gt 0 ]] || { echo "no foundation.sources in $SCRIPTS/win/witness-manifest.json" >&2; exit 2; }

if [[ "$REQUIRED_LEXICAL" -eq 1 ]]; then
  required=()
  for cli in "${FOUNDATION[@]}"; do
    case "$cli" in unicode|iso639|cili|wordnet) required+=("$cli") ;; esac
  done
  FOUNDATION=("${required[@]}")
  export LAPLACE_INGEST_FORCE=0 LAPLACE_INGEST_MAX_UNITS=0
  unset LAPLACE_INGEST_LANGS
fi

export LAPLACE_DBNAME="$DB"
export LAPLACE_DB="Host=${PGHOST};Username=${PGUSER};Database=${DB}"

needs_work=0
if [[ "$FORCE" -eq 1 || ( "$REQUIRED_LEXICAL" -eq 1 && "$CHECK_ONLY" -eq 0 ) ]]; then
  needs_work=1
else
  for cli in "${FOUNDATION[@]}"; do
    if ! layer_ok "$cli"; then
      needs_work=1
      break
    fi
  done
fi

if [[ "$needs_work" -eq 0 ]]; then
  echo "foundation layers OK on $DB"
  exit 0
fi

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  echo "foundation incomplete on $DB" >&2
  for cli in "${FOUNDATION[@]}"; do
    layer_ok "$cli" \
      || echo "  missing: ${cli} ($(layer_label "$cli"))" >&2
  done
  exit 1
fi

if [[ -n "${GITHUB_ACTIONS:-}${CI:-}" && -z "${LAPLACE_INGEST_CONSOLE:-}" ]]; then
  export LAPLACE_INGEST_CONSOLE=ci
fi

CHAIN=()
for cli in "${FOUNDATION[@]}"; do
  if [[ "$FORCE" -eq 1 || "$REQUIRED_LEXICAL" -eq 1 ]] || ! layer_ok "$cli"; then
    CHAIN+=("$cli")
  else
    echo "skip $cli (layer complete)"
  fi
done

if [[ ${#CHAIN[@]} -gt 0 ]]; then
  echo "ingest foundation chain (${#CHAIN[@]} source(s)): ${CHAIN[*]}"
  "$SCRIPTS/ingest-source.sh" chain "${CHAIN[@]}"
fi

echo "foundation journal (latest per source)"
psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -c \
  "SELECT DISTINCT ON (source_name) source_name, status, files_done, files_total,
          entities, attestations, ended_at
   FROM laplace.ingest_run_journal
   ORDER BY source_name, started_at DESC;"

remaining=0
for cli in "${FOUNDATION[@]}"; do
  if ! layer_ok "$cli"; then
    if [[ "$remaining" -eq 0 ]]; then
      echo "foundation incomplete after ingest on $DB" >&2
    fi
    echo "  missing: ${cli} ($(layer_label "$cli"))" >&2
    remaining=1
  fi
done
[[ "$remaining" -eq 0 ]] || exit 1

echo "foundation complete: $DB"
