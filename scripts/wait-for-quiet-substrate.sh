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
# WHY THE JOURNAL ALONE IS NOT ENOUGH. A run killed without ceremony (OOM, SIGKILL,
# cluster bounce, cancelled CI runner) never writes its terminal row, so its journal
# entry reads 'running' forever. MEASURED 2026-08-13: two UDDecomposer corpses sat
# 'running' for 8 and 6 hours with zero backends behind them and wedged every deploy
# behind this gate. So 'running' is only BELIEVED when the run also holds its
# liveness lock -- a session advisory lock keyed (LOCK_CLASS, hashtext(run_id)) that
# NpgsqlIngestObservability takes at run start on a dedicated connection. The server
# releases a session lock the instant the holding process dies, no cleanup code
# involved, which makes lock-presence the one liveness signal a corpse cannot fake.
# A 'running' row without its lock, observed twice across CORPSE_GRACE seconds (the
# grace covers the instant between the journal INSERT and the lock acquisition), is
# closed here as 'cancelled' and deploys stop being hostage to ghosts.
#
# It is also the only lock that works with more than one runner: GitHub concurrency
# groups cannot express "many ingests, one bouncer", and serialising ingest against
# ingest to fake it would freeze a single box's capacity into the pipeline.
set -euo pipefail

DB="${1:-${PGDATABASE:-laplace}}"
BUDGET_SECONDS="${2:-18000}"     # 5h: longer than the longest observed seed
INTERVAL=30
CORPSE_GRACE=90
# Must match NpgsqlIngestObservability.RunLivenessLockClass ("LPLK").
LOCK_CLASS=$(( 0x4C504C4B ))
# The unit whose liveness decides "down" vs "misconfigured" below. Must name the
# same cluster the caller is about to bounce, or the down-check proves nothing.
PG_SERVICE="${LAPLACE_PG_SERVICE:-laplace-postgresql.service}"

PSQL=(psql -h "${PGHOST:-/var/run/postgresql}" -U "${PGUSER:-laplace_admin}" -d "$DB")
deadline=$(( SECONDS + BUDGET_SECONDS ))
corpse_first_seen=""

HELD="EXISTS (SELECT 1 FROM pg_locks l
       WHERE l.locktype = 'advisory'
         AND l.database = (SELECT d.oid FROM pg_database d
                            WHERE d.datname = current_database())
         AND l.classid = ${LOCK_CLASS}::oid
         AND l.objsubid = 2
         AND l.objid::bigint = (hashtext(j.run_id::text)::bigint & 4294967295))"

while :; do
  # FAIL CLOSED. The previous form was `n=$(psql ... 2>/dev/null || echo 0)`, which
  # collapsed "the database says zero" and "the probe did not run" into the same
  # answer -- so an unreachable host, a bad PGUSER, a missing laplace schema or an
  # exhausted connection cap all reported QUIET and let the caller bounce PostgreSQL
  # over a live ingest. That is the one failure this script exists to prevent, and it
  # was the only one it could not see. Quiet must now be PROVEN: rc 0 and two numeric
  # counts. Anything else is busy, and the wait budget still bounds the loop.
  rc=0
  n=$("${PSQL[@]}" -tAc \
      "SELECT count(*) FILTER (WHERE ${HELD}) || ' ' || count(*) FILTER (WHERE NOT ${HELD})
       FROM laplace.ingest_run_journal j WHERE j.status = 'running';" 2>&1) || rc=$?

  if [ "$rc" -eq 0 ] && [[ "$n" =~ ^[0-9]+\ [0-9]+$ ]]; then
    live="${n% *}"
    corpses="${n#* }"

    if [ "$corpses" -gt 0 ]; then
      if [ -z "$corpse_first_seen" ]; then
        corpse_first_seen=$SECONDS
        echo "::warning::${corpses} 'running' row(s) hold no liveness lock — rechecking in ${CORPSE_GRACE}s before closing them as orphaned"
      elif [ $(( SECONDS - corpse_first_seen )) -ge "$CORPSE_GRACE" ]; then
        echo "::warning::closing ${corpses} orphaned run(s): 'running' in the journal, liveness lock absent for ${CORPSE_GRACE}s+ — no process is behind them"
        "${PSQL[@]}" -P pager=off -c \
          "UPDATE laplace.ingest_run_journal j SET status = 'cancelled', ended_at = now(),
                  error = 'run did not reach completion: liveness lock absent (cluster restart, OOM kill, or terminated session). Closed by wait-for-quiet-substrate.sh.'
           WHERE j.status = 'running' AND NOT ${HELD}
           RETURNING j.run_id, j.source_name, j.input_units_done, j.input_units_total;" || true
        corpse_first_seen=""
      fi
    else
      corpse_first_seen=""
    fi

    if [ "$live" -eq 0 ] && [ "$corpses" -eq 0 ]; then
      echo "substrate quiet — no ingest in flight"
      exit 0
    fi

    if [ "$live" -gt 0 ]; then
      echo "::notice::waiting on ${live} live ingest(s) before touching PostgreSQL"
      "${PSQL[@]}" -P pager=off -c \
        "SELECT source_name, input_units_done, input_units_total, now() - started_at AS elapsed
         FROM laplace.ingest_run_journal WHERE status = 'running' ORDER BY started_at;" || true
    fi
  elif [[ "$n" == *"3D000"* || "$n" == *"database \"$DB\" does not exist"* || "$n" == *"database $DB does not exist"* ]]; then
    # A database that DOES NOT EXIST is quiet, and it is the one non-answer that
    # proves it: SQLSTATE 3D000 means the server answered, authentication passed,
    # and there is no such database — so nothing can be ingesting into it. Failing
    # closed here waits the full budget for an ingest that cannot exist, which is
    # exactly what happens between an operator drop and the recreate that follows:
    # every deploy blocks ~5h on an absent database while the pipeline that would
    # rebuild it sits behind this gate. Reached the moment physicalities went
    # HASH(id), since that upgrade path IS drop-then-recreate.
    #
    # Narrow match only (Copilot #855): a missing schema/table/function that
    # happens to contain "does not exist" is NOT proof of quiet — those still
    # fall through to BUSY below. Only 3D000 / the database-missing phrasing
    # counts.
    echo "substrate quiet — database \"$DB\" does not exist, so no ingest can be running in it"
    exit 0
  elif command -v systemctl >/dev/null 2>&1 && ! systemctl is-active --quiet "$PG_SERVICE"; then
    # A STOPPED CLUSTER is quiet, and systemd — not psql — is what proves it.
    # Without this branch, a down postmaster is indistinguishable from a wrong
    # PGHOST, so both fell to BUSY below and the loop burned the entire 5h budget
    # waiting for an ingest that cannot exist (2026-08-11: "DB — recreate" spun
    # 26min against a socket that had no server behind it, and would have spun
    # until the job timeout). Waiting is only meaningful when something can
    # finish; a stopped server finishes nothing.
    #
    # Asking systemd rather than inferring from the psql error is what keeps this
    # fail-closed: a typo'd PGHOST or a dropped connection while the real cluster
    # is UP still falls through to BUSY, because the unit is active. And systemctl
    # itself must EXIST to testify (GH #1060): without the command -v guard, a
    # missing systemctl (rc 127) made `! systemctl is-active` true and declared a
    # cluster quiet on a box that cannot even ask — a broken observer reporting
    # the safest possible state. No systemctl means no systemd testimony, and the
    # probe failure falls through to BUSY where it belongs.
    echo "substrate quiet — $PG_SERVICE is not running, so no ingest can be in flight"
    exit 0
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
