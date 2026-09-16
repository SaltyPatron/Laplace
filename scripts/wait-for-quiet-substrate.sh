#!/usr/bin/env bash
# Wait until the substrate has no live ingest before a caller touches PostgreSQL.
# Canonical run liveness lives in ops.ingest_run_live / ops.ingest_reconcile_orphans;
# this script does not maintain a second lock/progress interpretation.
set -euo pipefail

DB="${1:-${PGDATABASE:-laplace}}"
BUDGET_SECONDS="${2:-18000}"
INTERVAL="${LAPLACE_QUIET_INTERVAL:-30}"
CORPSE_GRACE="${LAPLACE_CORPSE_GRACE:-90}"
PG_SERVICE="${LAPLACE_PG_SERVICE:-laplace-postgresql.service}"

[[ "$BUDGET_SECONDS" =~ ^[0-9]+$ ]] || { echo "invalid wait budget: $BUDGET_SECONDS" >&2; exit 2; }
[[ "$INTERVAL" =~ ^[0-9]+$ ]] || { echo "invalid quiet interval: $INTERVAL" >&2; exit 2; }
[[ "$CORPSE_GRACE" =~ ^[0-9]+$ ]] || { echo "invalid corpse grace: $CORPSE_GRACE" >&2; exit 2; }

PSQL=(psql -h "${PGHOST:-/var/run/postgresql}" -U "${PGUSER:-laplace_admin}" -d "$DB")
deadline=$(( SECONDS + BUDGET_SECONDS ))

while :; do
  probe_rc=0
  probe=$("${PSQL[@]}" -tAc \
    "SELECT CASE WHEN to_regclass('laplace.ingest_run_journal') IS NULL THEN 'missing' ELSE 'present' END;" \
    2>&1) || probe_rc=$?

  if [ "$probe_rc" -eq 0 ] && [ "$probe" = "missing" ]; then
    echo "substrate quiet — reachable database has no ingest journal"
    exit 0
  fi

  if [ "$probe_rc" -eq 0 ] && [ "$probe" = "present" ]; then
    state_rc=0
    state=$("${PSQL[@]}" -tAc \
      "SELECT count(*) FROM ops.ingest_reconcile_orphans(make_interval(secs => ${CORPSE_GRACE}));
       SELECT count(*) FROM laplace.ingest_run_journal j
        WHERE j.status = 'running'
          AND ops.ingest_run_live(j.run_id, make_interval(secs => ${CORPSE_GRACE}));" \
      2>&1) || state_rc=$?

    if [ "$state_rc" -eq 0 ]; then
      # psql emits one scalar per SELECT; the final non-empty line is the live count.
      live=$(printf '%s\n' "$state" | awk 'NF {value=$0} END {gsub(/^[[:space:]]+|[[:space:]]+$/, "", value); print value}')
      if [[ "$live" =~ ^[0-9]+$ ]]; then
        if [ "$live" -eq 0 ]; then
          echo "substrate quiet — no live ingest in flight"
          exit 0
        fi
        echo "::notice::waiting on ${live} live ingest(s) before touching PostgreSQL"
        "${PSQL[@]}" -P pager=off -c \
          "SELECT source_name, phase, files_done, files_total,
                  input_units_done, input_units_total, heartbeat_at,
                  now() - started_at AS elapsed
             FROM laplace.ingest_run_journal j
            WHERE j.status = 'running'
              AND ops.ingest_run_live(j.run_id, make_interval(secs => ${CORPSE_GRACE}))
            ORDER BY started_at;" || true
      else
        echo "::warning::canonical ingest-liveness query returned an unexpected response: ${state//$'\n'/ }"
      fi
    else
      echo "::warning::canonical ingest-liveness query failed: ${state//$'\n'/ }"
    fi
  elif [[ "$probe" == *"3D000"* || "$probe" == *"database \"$DB\" does not exist"* || "$probe" == *"database $DB does not exist"* ]]; then
    echo "substrate quiet — database \"$DB\" does not exist"
    exit 0
  elif command -v systemctl >/dev/null 2>&1 && ! systemctl is-active --quiet "$PG_SERVICE"; then
    echo "substrate quiet — $PG_SERVICE is not running"
    exit 0
  else
    echo "::warning::ingest-state probe failed (psql rc=${probe_rc}): ${probe//$'\n'/ }"
  fi

  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "::error::substrate not proven quiet after ${BUDGET_SECONDS}s — refusing to proceed"
    exit 1
  fi
  sleep "$INTERVAL"
done
