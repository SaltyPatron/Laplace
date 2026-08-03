#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPTS="$ROOT/scripts"

PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
FORCE=0
CHECK_ONLY=0

usage() {
  echo "Usage: $0 [--force|--check-only]" >&2
  echo "  --force       always re-ingest foundation sources (fresh_db path)" >&2
  echo "  --check-only  fail loud if any foundation layer is incomplete (no ingest; #792)" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force) FORCE=1; shift ;;
    --check-only) CHECK_ONLY=1; shift ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

# -d MUST precede -c. `psql -tAc -d DB SQL` makes -c consume "-d" as the
# query string (PG 18); layer markers then always look missing.
PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1)

db_exists() {
  "${PSQL[@]}" -d postgres -tAc \
    "SELECT 1 FROM pg_database WHERE datname='${DB}'" 2>/dev/null | grep -q 1
}

layer_ok() {
  local decomposer="$1" layer="$2"
  db_exists || return 1
  # psql -tA prints bool as t/f, not true/false.
  "${PSQL[@]}" -d "$DB" -tAc \
    "SELECT laplace.evidence_count(p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/${layer}/v1'), p_source => laplace.source_id('${decomposer}')) > 0;" \
    | grep -qiE '^(t|true)$'
}

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

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  echo "::error::THIN_SUBSTRATE: foundation HasLayerCompleted markers incomplete on $DB"
  for entry in "${FOUNDATION[@]}"; do
    IFS=':' read -r cli decomposer layer <<< "$entry"
    if ! layer_ok "$decomposer" "$layer"; then
      echo "  missing: ${cli} (source=${decomposer} layer=${layer})"
    fi
  done
  echo "Heal: dispatch seed-foundation / run scripts/ensure-foundation.sh — this check does not auto-reseed."
  exit 1
fi

echo "==== ensure-foundation on $DB ===="
# Journal is the pass/fail surface; keep Actions free of WS_APPLY / per-file spam.
if [[ -n "${GITHUB_ACTIONS:-}${CI:-}" && -z "${LAPLACE_INGEST_CONSOLE:-}" ]]; then
  export LAPLACE_INGEST_CONSOLE=ci
fi

for entry in "${FOUNDATION[@]}"; do
  IFS=':' read -r cli decomposer layer <<< "$entry"
  if [[ "$FORCE" -eq 1 ]] || ! layer_ok "$decomposer" "$layer"; then
    echo "==== ingest $cli ===="
    "$SCRIPTS/ingest-source.sh" "$cli"
    bash "$SCRIPTS/verify-ingest-journal.sh" "$decomposer"
  else
    echo "==== skip $cli (layer complete) ===="
  fi
done

echo "==== foundation journal (latest per source) ===="
psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -c \
  "SELECT DISTINCT ON (source_name) source_name, status, files_done, files_total,
          entities, attestations, ended_at
   FROM laplace.ingest_run_journal
   ORDER BY source_name, started_at DESC;"

echo "ENSURE-FOUNDATION COMPLETE: $DB"
