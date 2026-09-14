#!/usr/bin/env bash
# Named fail-loud gate for empty / thin / mid-ingest / structurally-invalid substrate.
#
# Fail modes (exact strings — grep these in CI logs / agent claims):
#   INVALID_INDEXES             — installed index-health operation reports an invalid index
#   INGEST_JOURNAL_NONTERMINAL  — status='running' row(s) in ingest_run_journal
#   THIN_SUBSTRATE              — foundation HasLayerCompleted markers incomplete
#                                 (or database missing)
#   RECURSIVE_SUBSTRATE_PROOF_* — exhaustive current content physicality/trajectory
#                                 contract proof failed; receipt has counterexamples
#
# Heal path depends on the failed coordinate. This script NEVER reseeds or repairs.
# Push/deploy/product proof must stay red until the selected live state is real.
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

invalid=$("${PSQL[@]}" -d "$DB" -tAc "SELECT count(*) FROM ops.index_health();")
if [[ "${invalid:-0}" -gt 0 ]]; then
  echo "::error::INVALID_INDEXES: ${invalid} invalid substrate index(es) on ${DB}"
  "${PSQL[@]}" -d "$DB" -P pager=off -c \
    "SELECT * FROM ops.index_health();" || true
  echo "Heal through the journaled index rebuild; its row is retained until validity is proven."
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

# The foundation marker proves expected source layers reached terminal admission. It
# does not prove that the recursively stored physicalities are internally sound.
# Run the read-only exhaustive finite-state proof after the journal is quiet so the
# receipt describes one stable estate rather than a moving ingest frontier.
proof_receipt="${LAPLACE_RECURSIVE_PROOF_RECEIPT:-$ROOT/build/test-receipts/live-recursive-substrate.json}"
if ! python3 "$ROOT/scripts/prove-live-recursive-substrate.py" "$DB" --receipt "$proof_receipt"; then
  echo "::error::RECURSIVE_SUBSTRATE_PROOF_FAIL: live recursive physicality/trajectory contract failed on ${DB}"
  echo "Receipt: $proof_receipt"
  exit 1
fi

echo "substrate floor OK on ${DB} (journal quiet + foundation layers complete + recursive proof green)"
exit 0
