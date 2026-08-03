#!/usr/bin/env bash
# Assert the durable ingest ledger — not the Actions log — for a source.
#
# Usage:
#   scripts/verify-ingest-journal.sh <SourceName>           # e.g. FrameNetDecomposer
#   scripts/verify-ingest-journal.sh --cli-key framenet    # resolve via decomposer-gates.json
#
# Exit 0 iff the latest ingest_run_journal row for that source is a successful
# terminal status — see the case block at the bottom for the list and why each is there.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"

usage() {
  echo "Usage: $0 <SourceName> | --cli-key <ingest-source.sh key>" >&2
  exit 2
}

SOURCE=""
if [[ "${1:-}" == "--cli-key" ]]; then
  [[ $# -ge 2 ]] || usage
  key="$2"
  SOURCE=$(python3 -c "
import json, sys
e = json.load(open('${ROOT}/scripts/decomposer-gates.json'))['sources'].get(sys.argv[1])
print(e['decomposer'] if e else '')
" "$key")
  [[ -n "$SOURCE" ]] || { echo "error: no decomposer-gates.json entry for '$key'" >&2; exit 2; }
elif [[ $# -ge 1 && "$1" != -* ]]; then
  SOURCE="$1"
else
  usage
fi

PSQL=(psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -tAc)

# Fully-qualify — a leading SET under -tAc prints "SET" as the first result row.
row=$("${PSQL[@]}" "
SELECT status || '|' || coalesce(files_done::text,'') || '|' || coalesce(files_total::text,'')
    || '|' || coalesce(entities::text,'') || '|' || coalesce(attestations::text,'')
    || '|' || coalesce(error,'')
FROM laplace.ingest_run_journal
WHERE source_name = '${SOURCE}'
ORDER BY started_at DESC
LIMIT 1;
")

if [[ -z "$row" ]]; then
  echo "error: no ingest_run_journal row for source_name=${SOURCE} on ${DB}" >&2
  exit 1
fi

IFS='|' read -r status files_done files_total entities attestations error <<<"$row"
echo "JOURNAL source=${SOURCE} db=${DB} status=${status} files=${files_done}/${files_total} entities=${entities} attestations=${attestations}"

# Terminal statuses that mean the lane did its job.
#
#   ok                 wrote what it read
#   skipped-complete   source completion marker already present
#   capped             deliberate MaxInputUnits smoke run
#   already-present    read records, novelty gate proved every one present (idempotent re-ingest)
#   already-complete   marker-gated backfill with nothing left to do
#   dependency-unset   optional dependency absent; documented no-op (syzygy tablebases)
#
# empty-noop is NOT here. The runner THROWS on it — it means the source declared input,
# applied none, and could not account for it (IIngestNoOpExplainer). Accepting it as
# success made this verifier contradict the process that wrote the row: the CLI exited 1
# and the ledger check said the run was fine.
case "$status" in
  ok|skipped-complete|capped|already-present|already-complete|dependency-unset)
    exit 0
    ;;
  *)
    echo "error: latest journal status for ${SOURCE} is '${status}'" >&2
    echo "       (want ok|skipped-complete|capped|already-present|already-complete|dependency-unset)" >&2
    [[ -n "$error" ]] && echo "error detail: $error" >&2
    exit 1
    ;;
esac
