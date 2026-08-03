#!/usr/bin/env bash
# Named fail-loud gate for empty / thin / mid-ingest substrate (#792).
#
# Fail modes (exact strings — grep these in CI logs / agent claims):
#   INGEST_JOURNAL_NONTERMINAL  — status='running' row(s) in ingest_run_journal
#   THIN_SUBSTRATE              — foundation HasLayerCompleted markers incomplete
#                                 (or database missing)
#
# Heal path: dispatch seed-foundation (or wait for the in-flight ingest). This
# script NEVER reseeds. Push/deploy must stay red until the floor is real —
# silent skip / greenwash of conversational claims is the defect.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DB="${1:-${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}}"
PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"

# -d before -c (see ensure-foundation.sh); -tAc must not precede -d.
PSQL=(psql -h "$PGHOST" -U "$PGUSER" -v ON_ERROR_STOP=1)

if ! "${PSQL[@]}" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='${DB}'" 2>/dev/null | grep -q 1; then
  echo "::error::THIN_SUBSTRATE: database '${DB}' does not exist — heal via seed-foundation / fresh_db (no auto-reseed)"
  exit 1
fi

running=$("${PSQL[@]}" -d "$DB" -tAc "SELECT count(*) FROM laplace.ingest_run_journal WHERE status = 'running';")
if [[ "${running:-0}" -gt 0 ]]; then
  echo "::error::INGEST_JOURNAL_NONTERMINAL: ${running} ingest_run_journal row(s) still status=running on ${DB}"
  psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -P pager=off -c \
    "SELECT source_name, status, input_units_done, input_units_total, now() - started_at AS elapsed, error
     FROM laplace.ingest_run_journal WHERE status = 'running' ORDER BY started_at;" || true
  echo "Heal: wait for the run to terminal, or mark a true orphan cancelled/failed — do not claim live results over it."
  exit 1
fi

# Same layer roster as ensure-foundation.sh — --check-only never ingests.
export LAPLACE_DBNAME="$DB"
export PGHOST PGUSER
if ! bash "$ROOT/scripts/ensure-foundation.sh" --check-only; then
  echo "Heal: gh workflow run seed-foundation.yml --ref main   (or scripts/ensure-foundation.sh). No auto-reseed from this gate."
  exit 1
fi

echo "substrate floor OK on ${DB} (journal quiet + foundation layers complete)"
exit 0
