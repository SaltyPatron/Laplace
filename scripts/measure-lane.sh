#!/usr/bin/env bash
# Run a measurement holding the EXCLUSIVE measurement lane, so nothing writes to the
# substrate while it runs. Non-zero if the lane cannot be held.
#
#   measure-lane.sh -c "EXPLAIN (ANALYZE, BUFFERS) SELECT ..."
#   measure-lane.sh -f bench/read-path.sql
#   measure-lane.sh -- just bench-ingest --source omw
#
# WHY THIS EXISTS, MEASURED. A wall-clock number taken while an ingest is writing is not
# a measurement of the code. On 2026-08-15 generation.compose_batch('what is a wolf', 12)
# returned 264,605 / 244,533 / 81,701 / 316,998 / 144,256 / 144,947 / 153,203 / 301,571 ms
# across one session for near-identical code, with laplace.ingest_run_journal showing an
# active run throughout -- a 3.9x spread, and causal claims were drawn from single runs
# inside it. Re-measured on a quiet database the same surfaces read
# realize.resolve_name 36,000 -> 0 ms and generation.separator_ids 9,450 -> 11 ms with NO
# code change: both had been recorded as defects, and both were contention. The cost is
# not the wasted session -- it is the code written against those numbers.
#
# WHY A LOCK AND NOT A CHECK. docs/sql-refactor-tasklist.md already states the
# precondition ("confirm no ingest run is active, then measure each variant at least 3
# times") as a discipline. A discipline is checked once at the start; an ingest
# dispatched one minute later still lands mid-measurement, and the resulting number looks
# exactly like a valid one. Mutual exclusion has to be a property of the system.
#
# WHY THE LOCK IS NOT TAKEN HERE. The first version of this script issued its own
# pg_advisory_lock from bash, and IngestMutexGateTests rejected it: a second
# hand-rolled database lock is a second mutex, and that gate's allowlist is shrink-only.
# It was right. Acquisition lives in AdvisoryTxLock (the one sanctioned home) and is
# reached through `laplace measure-lane`, so there is ONE definition of the lane's class,
# key and modes and no shell copy that can drift from it. This file is a convenience
# wrapper: it adds the SQL modes and nothing else.
set -euo pipefail

CLI="${LAPLACE_CLI:-$(dirname "$0")/../app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli}"
[ -x "$CLI" ] || CLI="dotnet run --project $(dirname "$0")/../app/Laplace.Cli/Laplace.Cli.csproj --"

usage() { sed -n '2,8p' "$0" >&2; exit 2; }

case "${1:-}" in
  -c) shift; stmt="${1:?-c needs a statement}"
      exec $CLI measure-lane psql -v ON_ERROR_STOP=1 -c "\timing on" -c "$stmt" ;;
  -f) shift; path="${1:?-f needs a path}"
      [ -r "$path" ] || { echo "::error::unreadable: $path" >&2; exit 2; }
      exec $CLI measure-lane psql -v ON_ERROR_STOP=1 -c "\timing on" -f "$path" ;;
  --) shift; [ $# -gt 0 ] || usage
      exec $CLI measure-lane "$@" ;;
  *)  usage ;;
esac
