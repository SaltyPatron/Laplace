#!/usr/bin/env bash
# Decide whether a non-zero ingest was PREEMPTED or FAILED, and print exactly one of:
#
#   preempted   the cluster went away underneath a running seed
#   failed      anything else
#
#   classify-ingest-exit.sh <detail-log> [run-started-epoch]
#
# This is diagnostic classification only. The caller preserves the nonzero process
# exit code for both outcomes; neither outcome certifies a completed ingest.
set -euo pipefail

LOG="${1:?usage: classify-ingest-exit.sh <detail-log> [run-started-epoch]}"
STARTED="${2:-}"

# SQLSTATEs that mean "the server took itself away", and nothing else. These are not
# ambiguous the way a message string is: 57P01 admin_shutdown (terminating connection due
# to administrator command), 57P02 crash_shutdown, 57P03 cannot_connect_now (the database
# system is shutting down / starting up). A decomposer defect cannot raise them.
SQLSTATES='57P01|57P02|57P03'

# Message forms, for the paths that log a message without its SQLSTATE.
MESSAGES='the database system is shutting down|the database system is starting up|terminating connection due to administrator command|server closed the connection unexpectedly'

sig=0
if [ -r "$LOG" ] && grep -qE "$SQLSTATES|$MESSAGES" "$LOG" 2>/dev/null; then
  sig=1
fi

# CORROBORATE WITH THE POSTMASTER, which cannot be forged by log contents. If the cluster
# is reachable and its start time is NEWER than this run's start, it genuinely restarted
# underneath the run. This is what keeps a log line from being enough on its own: a
# corpus that happens to contain "57P03" as data cannot manufacture a restart.
#
# Absence of corroboration is NOT a veto. The most common preemption leaves the server
# down or still starting, so the probe cannot answer at all -- and refusing to classify
# then would fail exactly the case this exists for. Corroboration upgrades confidence; the
# SQLSTATE carries the decision.
restarted=unknown
if [ -n "$STARTED" ]; then
  pmst=$(psql -h "${PGHOST:-/var/run/postgresql}" -U "${PGUSER:-laplace_admin}" \
              -d "${PGDATABASE:-laplace}" -tAc \
              "SELECT floor(extract(epoch FROM pg_postmaster_start_time()))::bigint;" 2>/dev/null || true)
  if [[ "$pmst" =~ ^[0-9]+$ ]]; then
    if [ "$pmst" -ge "$STARTED" ]; then restarted=yes; else restarted=no; fi
  fi
fi

# A reachable cluster that has NOT restarted since the run began is decisive the other
# way: whatever killed the ingest, it was not a bounce. Without this, any log that merely
# mentions a shutdown string would be excused forever.
if [ "$sig" -eq 1 ] && [ "$restarted" != "no" ]; then
  echo preempted
else
  echo failed
fi
