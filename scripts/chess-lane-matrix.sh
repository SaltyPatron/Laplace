#!/usr/bin/env bash
# Chess decomposer lane matrix against a THROWAWAY database.
#
# Every chess lane, end to end, on a database that is dropped and recreated first, so a
# result never depends on what a previous run left behind. Covers the cases that used to
# exit 0 having written nothing: empty directory, wrong extensions, corpus one level
# down, corpus still inside archives.
#
#   scripts/chess-lane-matrix.sh [--db NAME] [--keep] [--corpus DIR]
#
# --keep leaves the database up for inspection. Default drops it on the way in, not on
# the way out, so the last run's substrate is always available to query.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DB="laplace_chess_lane_test"
CORPUS=""
KEEP=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --db)     DB="$2"; shift 2 ;;
    --corpus) CORPUS="$2"; shift 2 ;;
    --keep)   KEEP=1; shift ;;
    *) echo "unknown flag: $1" >&2; exit 2 ;;
  esac
done

export PGHOST="${PGHOST:-/var/run/postgresql}"
export PGUSER="${PGUSER:-laplace_admin}"
DATA_ROOT="${LAPLACE_DATA_ROOT:-/vault/Data}"
CHESS="$DATA_ROOT/Games/Chess"

export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"

# A sampled corpus keeps the matrix under a few minutes. The point is lane behaviour,
# not throughput — throughput is measured on the full corpora by the seed workflows.
if [[ -z "$CORPUS" ]]; then
  CORPUS="$(mktemp -d)/chess-matrix"
  mkdir -p "$CORPUS"/{pgn,openings,books,empty,wrong,nested/otb,archives}
  head -c 3000000 "$CHESS/twic/twic1620.pgn"                              > "$CORPUS/pgn/twic-sample.pgn"
  head -c 2000000 "$CHESS/Lumbras/otb/LumbrasGigaBase_OTB_1900-1949.pgn"  > "$CORPUS/pgn/lumbras-otb-sample.pgn"
  cp "$CHESS/chess_com_games_2026-06-25.pgn"                                "$CORPUS/pgn/"
  cp "$CHESS/openings"/*.tsv                                               "$CORPUS/openings/"
  cp "$DATA_ROOT/test-data/text/the-blue-book-of-chess.txt"                 "$CORPUS/books/"
  cp "$DATA_ROOT/test-data/text/chess-fundamentals.txt"                     "$CORPUS/books/" 2>/dev/null || true
  cp "$DATA_ROOT/test-data/text/chess-strategy.txt"                         "$CORPUS/books/" 2>/dev/null || true
  echo "not,really,a,pgn"                                                 > "$CORPUS/wrong/games.csv"
  head -c 200000 "$CHESS/twic/twic1620.pgn"                               > "$CORPUS/nested/otb/games.pgn"
  cp "$CHESS/Lumbras/LumbrasGigaBase_OTB_2025.7z"                          "$CORPUS/archives/" 2>/dev/null \
    || echo "fake" > "$CORPUS/archives/corpus.7z"
  echo ">>> sampled corpus at $CORPUS"
fi

echo "==== recreating $DB ===="
bash "$ROOT/scripts/decomposer-isolate.sh" "$DB" >/dev/null
export LAPLACE_DBNAME="$DB"
export LAPLACE_DB="Host=${PGHOST};Username=${PGUSER};Database=${DB}"

# The isolated DB is built from the INSTALLED extension, which lags the working tree
# until `build-extensions && install-extensions` runs (that bounces PostgreSQL, so it
# waits for a quiet box). Re-apply the ingest_run_journal status constraint straight
# from the schema of record so this matrix tests the CURRENT contract rather than the
# last-installed one — the block is read out of the .sql.in, never retyped, so the two
# cannot drift.
JOURNAL_DDL="$ROOT/extension/laplace_substrate/sql/schema/tables/ingest_run_journal.sql.in"
if [[ -f "$JOURNAL_DDL" ]]; then
  { echo "SET search_path = laplace, public;"
    sed -n '/^DO \$\$/,/^END \$\$;/p' "$JOURNAL_DDL"
  } | psql -v ON_ERROR_STOP=1 -d "$DB" -q
fi

pass=0; fail=0
# expect_ok / expect_fail assert the EXIT CODE, which is the whole point: a lane that
# reads nothing must not report success, and a lane handed a real corpus must not error.
lane() {
  local expect="$1"; shift
  local label="$1"; shift
  local out rc=0
  out="$(cd "$ROOT/app" && timeout 3600 dotnet "$DLL" ingest "$@" 2>&1)" || rc=$?
  if [[ "$expect" == ok && "$rc" -eq 0 ]] || [[ "$expect" == fail && "$rc" -ne 0 ]]; then
    echo "  PASS  [$expect rc=$rc] $label"
    pass=$((pass + 1))
  else
    echo "  FAIL  [want $expect got rc=$rc] $label"
    echo "$out" | tail -20 | sed 's/^/        /'
    fail=$((fail + 1))
  fi
  # Surface the lane's own accounting whatever the verdict.
  echo "$out" | grep -E "INGEST_COMPLETE|CHESS_DROPPED|no input files|--recursive|7z x|Extensions present" \
    | sed 's/^/        /' || true
}

echo "==== lanes that must FAIL (nothing to read) ===="
lane fail "empty directory"          chess       "$CORPUS/empty"
lane fail "wrong extensions"         chess       "$CORPUS/wrong"
lane fail "corpus one level down"    chess       "$CORPUS/nested"
lane fail "corpus still in archives" chess       "$CORPUS/archives"
lane fail "openings: empty"          openings    "$CORPUS/empty"
lane fail "books: empty"             chess-books "$CORPUS/empty"
lane fail "missing path"             chess       "$CORPUS/does-not-exist"

echo "==== lanes that must SUCCEED ===="
lane ok "openings (ECO TSV)"      openings    "$CORPUS/openings"
lane ok "pgn (twic + lumbras + chess.com)" chess "$CORPUS/pgn"
lane ok "books"                   chess-books "$CORPUS/books"
lane ok "nested with --recursive" chess       "$CORPUS/nested" --recursive
lane ok "syzygy probe"            chess-syzygy
lane ok "trajectory backfill"     chess-trajectory

echo "==== durable ledger (the journal, not the exit code) ===="
# Exit 0 is the process's opinion. This is the row the CI verifier reads, and the two
# disagreeing is exactly how a run reported success while its journal said 'running'.
for key in chess openings chess-books chess-syzygy chess-trajectory; do
  if bash "$ROOT/scripts/verify-ingest-journal.sh" --cli-key "$key" >/tmp/journal-$$.txt 2>&1; then
    echo "  PASS  journal $key: $(tail -1 /tmp/journal-$$.txt)"
    pass=$((pass + 1))
  else
    echo "  FAIL  journal $key"; sed 's/^/        /' /tmp/journal-$$.txt
    fail=$((fail + 1))
  fi
done
rm -f /tmp/journal-$$.txt

echo "==== per-source gates (decomposer-gates.json) ===="
for key in chess openings chess-books chess-syzygy chess-trajectory; do
  if python3 "$ROOT/scripts/decomposer-gate-check.py" --source "$key" --dbname "$DB" \
       --user "$PGUSER" --host "$PGHOST" >/tmp/gate-$$.txt 2>&1; then
    echo "  PASS  gates $key"
    pass=$((pass + 1))
  else
    echo "  FAIL  gates $key"; tail -15 /tmp/gate-$$.txt | sed 's/^/        /'
    fail=$((fail + 1))
  fi
done
rm -f /tmp/gate-$$.txt

echo "==== substrate ===="
psql -d "$DB" -P pager=off -c "SET search_path = laplace, public; SELECT * FROM source_counts();" 2>/dev/null || true

if [[ "$KEEP" == 0 ]]; then
  echo "(database $DB left up for inspection; next run drops it)"
fi

echo "==== chess-lane-matrix: $pass passed, $fail failed ===="
[[ "$fail" -eq 0 ]]
