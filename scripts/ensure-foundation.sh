#!/usr/bin/env bash
# CI foundation seed — order from scripts/win/witness-manifest.json foundation.sources
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"

PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
FORCE=0

usage() {
  echo "Usage: $0 [--force]" >&2
  echo "  --force  always re-ingest foundation sources (fresh_db path)" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force) FORCE=1; shift ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1 -tAc)

db_exists() {
  "${PSQL[@]}" -d postgres \
    "SELECT 1 FROM pg_database WHERE datname='${DB}'" 2>/dev/null | grep -q 1
}

layer_ok() {
  local decomposer="$1" layer="$2"
  db_exists || return 1
  "${PSQL[@]}" -d "$DB" \
    "SELECT laplace.evidence_count(p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/${layer}/v1'), p_source => laplace.source_id('${decomposer}')) > 0;" \
    | grep -qi true
}

# cli:decomposer:layer — must match scripts/win/seed-foundation.cmd and witness-manifest.json
FOUNDATION=(
  "unicode:UnicodeDecomposer:0"
  "iso639:ISO639Decomposer:1"
  "cili:CILIDecomposer:2"
  "wordnet:WordNetDecomposer:2"
  "verbnet:VerbNetDecomposer:2"
  "propbank:PropBankDecomposer:2"
  "framenet:FrameNetDecomposer:3"
  "mapnet:MapNetDecomposer:3"
  "wordframenet:WordFrameNetDecomposer:3"
  "semlink:SemLinkDecomposer:3"
)

export LAPLACE_DBNAME="$DB"
export LAPLACE_DB="Host=${PGHOST};Username=${PGUSER};Database=${DB}"

needs_work=0
if [[ "$FORCE" -eq 1 ]]; then
  needs_work=1
else
  for entry in "${FOUNDATION[@]}"; do
    IFS=':' read -r _cli decomposer layer <<< "$entry"
    if ! layer_ok "$decomposer" "$layer"; then
      needs_work=1
      break
    fi
  done
fi

if [[ "$needs_work" -eq 0 ]]; then
  echo "foundation layers OK on $DB"
  exit 0
fi

echo "==== ensure-foundation on $DB ===="

for entry in "${FOUNDATION[@]}"; do
  IFS=':' read -r cli decomposer layer <<< "$entry"
  if [[ "$FORCE" -eq 1 ]] || ! layer_ok "$decomposer" "$layer"; then
    echo "==== ingest $cli ===="
    "$SCRIPTS/ingest-source.sh" "$cli"
  else
    echo "==== skip $cli (layer complete) ===="
  fi
done

echo "ENSURE-FOUNDATION COMPLETE: $DB"
