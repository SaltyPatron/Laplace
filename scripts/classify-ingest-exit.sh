#!/usr/bin/env bash
# Decide whether a non-zero ingest was PREEMPTED or FAILED, and print exactly one of:
#
#   preempted   the cluster went away underneath a running seed
#   failed      anything else
#
#   classify-ingest-exit.sh <detail-log> [run-started-epoch]
#
# WHY THIS IS A SEPARATE FILE. It is the one piece of S5 that can be wrong in a way
# nobody notices: classifying a real defect as "preempted" hides it, and that is strictly
# worse than the red it replaces. A classifier inlined in a workflow step is a classifier
# nobody can run against a fixture. This one is a pure function of a log file, so it is
# testable, and scripts/tests/classify-ingest-exit.test.sh proves both directions.
#
# WHAT IT IS FOR. MEASURED 2026-08-15 (docs/sql-refactor-tasklist.md §S5): a chess seed
# died at 22:36:41 to
#     sudo systemctl restart laplace-postgresql.service
# and reported `failure` after 1 s with 57P03. .github/workflows/laplace.yml:17-19 already states that
# rebuilds preempt seeds BY DESIGN and that seed steps are idempotent/resumable, so "a
# preempted seed loses nothing and re-runs cleanly". A run that the workflow's own header
# calls expected must not be indistinguishable from a broken decomposer -- that is how a
# 0%-green seed lane stops carrying information.
#
# PREEMPTED IS NOT SUCCESS. It only suppresses the red. The caller must still refuse to
# certify: nothing downstream may read a preempted run as a completed seed.
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
