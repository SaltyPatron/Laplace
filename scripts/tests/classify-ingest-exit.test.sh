#!/usr/bin/env bash
# Fixtures for classify-ingest-exit.sh, both directions.
#
# The direction that matters is FALSE PREEMPTION: calling a real defect "preempted" hides
# it, which is worse than the red it replaces. Most cases below are therefore logs that
# must still classify as failed.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CLASSIFY="$HERE/../classify-ingest-exit.sh"
pass=0; fail=0

check() {
  local name="$1" expect="$2" log="$3" started="${4:-}"
  local f; f="$(mktemp)"; printf '%s\n' "$log" > "$f"
  local got; got="$("$CLASSIFY" "$f" "$started" 2>/dev/null || echo ERROR)"
  if [ "$got" = "$expect" ]; then
    pass=$((pass+1)); printf '  ok    %-52s -> %s\n' "$name" "$got"
  else
    fail=$((fail+1)); printf '  FAIL  %-52s -> %s (expected %s)\n' "$name" "$got" "$expect"
  fi
  rm -f "$f"
}

echo "classify-ingest-exit.sh"

# --- preemption: the case S5 exists for -------------------------------------------------
check "57P03 shutting down (the S5 incident)" preempted \
  'Npgsql.PostgresException (0x80004005): 57P03: the database system is shutting down'
check "57P01 admin terminate" preempted \
  'FATAL: 57P01: terminating connection due to administrator command'
check "57P02 crash shutdown" preempted \
  'FATAL: 57P02: the database system is in recovery mode'
check "message without a SQLSTATE" preempted \
  'Exception while reading from stream: server closed the connection unexpectedly'
check "starting up (bounce, came back too slow)" preempted \
  'FATAL: the database system is starting up'

# --- real defects that must STAY red ----------------------------------------------------
check "decomposer null deref" failed \
  'Unhandled exception. System.NullReferenceException at UDDecomposer.Parse(line 44)'
check "OOM kill (S4 — a real defect, not a bounce)" failed \
  'Out of memory: Killed process 2282897 (dotnet) total-vm:381196684kB'
check "FK violation" failed \
  '23503: insert or update on table "attestations" violates foreign key constraint'
check "unique violation" failed \
  'Npgsql.PostgresException: 23505: duplicate key value violates unique constraint'
check "empty log" failed ''
check "timeout, no shutdown signature" failed \
  'The operation has timed out after 600 seconds'
check "lock timeout (55P03 — near miss on the digits)" failed \
  'Npgsql.PostgresException: 55P03: lock_timeout expired'
check "a SQLSTATE-shaped string in corpus data" failed \
  'ingested sentence: "error 57P03 appeared in the manual"' "$(date +%s)"

# --- corroboration ----------------------------------------------------------------------
# With a start time in the FUTURE, the postmaster cannot have restarted since -- the
# probe answers "no" and vetoes an otherwise-matching signature.
check "shutdown signature but no restart since (veto)" failed \
  'FATAL: 57P03: the database system is shutting down' "$(( $(date +%s) + 86400 ))"
# With a start time far in the past, a running postmaster started after it -> corroborated.
check "shutdown signature and cluster restarted since" preempted \
  'FATAL: 57P03: the database system is shutting down' "1"

echo
echo "  passed=$pass failed=$fail"
[ "$fail" -eq 0 ]
