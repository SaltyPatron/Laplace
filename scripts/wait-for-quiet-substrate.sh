#!/usr/bin/env bash
# Block until no ingest is in flight, then exit 0. Non-zero if one is still running
# when the wait budget expires -- the caller is about to bounce PostgreSQL and must
# not do it over a live ingest.
#
# WHY THE JOURNAL AND NOT pg_stat_activity. The first version of this check (inline in
# db-ops.yml) looked for an active COPY:
#
#     SELECT count(*) FROM pg_stat_activity
#     WHERE state='active' AND query ILIKE 'COPY %'
#
# An ingest only COPYs in BURSTS. A measured chess run flushed roughly once every 50
# seconds and spent the rest of the time composing in memory, so that query reports
# "no ingest" for minutes at a stretch while an ingest is very much running -- and the
# nuke lands in the gap. laplace.ingest_run_journal carries status='running' for the
# whole run, start to terminal status, across every workflow and every dispatcher.
#
# It is also the only lock that works with more than one runner: GitHub concurrency
# groups cannot express "many ingests, one bouncer", and serialising ingest against
# ingest to fake it would freeze a single box's capacity into the pipeline.
set -euo pipefail

DB="${1:-${PGDATABASE:-laplace}}"
BUDGET_SECONDS="${2:-18000}"     # 5h: longer than the longest observed seed
INTERVAL=30

PSQL=(psql -h "${PGHOST:-/var/run/postgresql}" -U "${PGUSER:-laplace_admin}" -d "$DB")
deadline=$(( SECONDS + BUDGET_SECONDS ))

while :; do
  # FAIL CLOSED. The previous form was `n=$(psql ... 2>/dev/null || echo 0)`, which
  # collapsed "the database says zero" and "the probe did not run" into the same
  # answer -- so an unreachable host, a bad PGUSER, a missing laplace schema or an
  # exhausted connection cap all reported QUIET and let the caller bounce PostgreSQL
  # over a live ingest. That is the one failure this script exists to prevent, and it
  # was the only one it could not see. Quiet must now be PROVEN: rc 0 and a numeric
  # count. Anything else is busy, and the wait budget still bounds the loop.
  rc=0
  n=$("${PSQL[@]}" -tAc \
      "SELECT count(*) FROM laplace.ingest_run_journal WHERE status = 'running';" 2>&1) || rc=$?

  if [ "$rc" -eq 0 ] && [[ "$n" =~ ^[0-9]+$ ]]; then
    if [ "$n" -eq 0 ]; then
      echo "substrate quiet — no ingest in flight"
      exit 0
    fi

    echo "::notice::waiting on ${n} in-flight ingest(s) before touching PostgreSQL"
    "${PSQL[@]}" -P pager=off -c \
      "SELECT source_name, input_units_done, input_units_total, now() - started_at AS elapsed
       FROM laplace.ingest_run_journal WHERE status = 'running' ORDER BY started_at;" || true
  else
    echo "::warning::ingest-state probe failed (psql rc=${rc}): ${n//$'\n'/ }"
    echo "::warning::treating as BUSY — an unreachable or misconfigured database is not proof of quiet"
  fi

  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "::error::substrate not PROVEN quiet after ${BUDGET_SECONDS}s — refusing to proceed"
    exit 1
  fi
  sleep "$INTERVAL"
done
