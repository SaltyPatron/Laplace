#!/usr/bin/env bash
# Rank what an ingest actually spent its time on.
#
# Until pg_stat_statements landed there was no way to ask this, so "the seed is slow"
# was argued from wait events and plan shapes -- both of which describe a SYMPTOM and
# neither of which ranks anything. Several confident diagnoses died on contact with a
# measurement that was always available.
#
#   statement-profile.sh reset          # zero the counters, immediately before a run
#   statement-profile.sh top [N]        # rank by total time (plan + execute)
#   statement-profile.sh calls [N]      # rank by call frequency
#   statement-profile.sh snapshot       # JSON evidence without resetting counters
#   statement-profile.sh plans [N]      # rank by PLANNING time -- the re-planning tax
#   statement-profile.sh io [N]         # rank by blocks read -- the lookup/index tax
#
# Counters are shared evidence: preserve them during live investigations. Capture
# snapshots before/after a workload and compare matching (dbid, userid, queryid,
# toplevel) entries; reset timestamps and eviction counts bound that comparison.
# Nested times overlap their callers and must never be summed as disjoint work.

set -euo pipefail

PSQL=(psql -U "${PGUSER:-laplace_admin}" -d "${PGDATABASE:-laplace}" -qAX -v ON_ERROR_STOP=1
      -c "SET search_path = laplace, public;")
[[ -n "${PGHOST:-}" ]] && PSQL=(psql -h "$PGHOST" -U "${PGUSER:-laplace_admin}" -d "${PGDATABASE:-laplace}" -qAX -v ON_ERROR_STOP=1
      -c "SET search_path = laplace, public;")
N="${2:-20}"
[[ "$N" =~ ^[1-9][0-9]*$ ]] || { echo "N must be a positive integer" >&2; exit 2; }

have() {
    "${PSQL[@]}" -tAc "SELECT 1 FROM pg_extension WHERE extname='pg_stat_statements'" 2>/dev/null | grep -q 1
}

if ! have; then
    echo "pg_stat_statements is not installed in ${PGDATABASE:-laplace}." >&2
    echo "It needs shared_preload_libraries + a postmaster restart (tune-pg does this)." >&2
    exit 2
fi

case "${1:-top}" in
reset)
    "${PSQL[@]}" -c "SELECT pg_stat_statements_reset();" >/dev/null
    echo "statement counters reset — start the run now"
    ;;

snapshot)
    "${PSQL[@]}" -t -c "
      SELECT json_build_object(
        'captured_at', clock_timestamp(), 'database', current_database(),
        'server_version', version(),
        'info', (SELECT row_to_json(i) FROM pg_stat_statements_info i),
        'statements', (SELECT json_agg(s) FROM pg_stat_statements s
          WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())));"
    ;;

calls)
    "${PSQL[@]}" -c "
      SELECT queryid, toplevel, calls,
             round(total_exec_time::numeric, 1) AS exec_ms,
             round(mean_exec_time::numeric, 3) AS mean_exec_ms,
             round(max_exec_time::numeric, 3) AS max_exec_ms,
             rows, left(regexp_replace(query, '\s+', ' ', 'g'), 160) AS statement
      FROM pg_stat_statements
      WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
      ORDER BY calls DESC, total_exec_time DESC LIMIT $N;"
    ;;

top)
    # total_plan_time is only populated when pg_stat_statements.track_planning = on.
    # Reported separately rather than folded in: a statement dominated by PLANNING is a
    # plan-cache problem, and one dominated by EXECUTION is a query/index problem. They
    # have different fixes, so summing them hides the answer.
    "${PSQL[@]}" -c "
      SELECT round((total_plan_time + total_exec_time)::numeric) AS total_ms,
             round(total_plan_time::numeric)                     AS plan_ms,
             round(total_exec_time::numeric)                     AS exec_ms,
             queryid, toplevel, calls,
             round(mean_exec_time::numeric, 3)                   AS mean_exec_ms,
             rows,
             left(regexp_replace(query, '\s+', ' ', 'g'), 90)    AS statement
      FROM pg_stat_statements
      WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
      ORDER BY (total_plan_time + total_exec_time) DESC
      LIMIT $N;"
    ;;

plans)
    "${PSQL[@]}" -c "
      SELECT round(total_plan_time::numeric)                 AS plan_ms,
             round(total_exec_time::numeric)                 AS exec_ms,
             CASE WHEN total_exec_time > 0
                  THEN round((100*total_plan_time/(total_plan_time+total_exec_time))::numeric,1)
             END                                             AS pct_planning,
             queryid, toplevel, calls,
             left(regexp_replace(query, '\s+', ' ', 'g'), 90) AS statement
      FROM pg_stat_statements
      WHERE total_plan_time > 0
        AND dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
      ORDER BY total_plan_time DESC
      LIMIT $N;"
    ;;

io)
    # shared_blks_read is what actually left the buffer cache. A probe that seq-scans a
    # partitioned table shows up here long before it shows up in wall-clock, because the
    # pages are usually cached on a warm box and the cost only appears under real volume.
    "${PSQL[@]}" -c "
      SELECT queryid, toplevel, shared_blks_read, shared_blks_hit, calls,
             round(total_exec_time::numeric) AS exec_ms,
             left(regexp_replace(query, '\s+', ' ', 'g'), 90) AS statement
      FROM pg_stat_statements
      WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
      ORDER BY shared_blks_read DESC
      LIMIT $N;"
    ;;

*)
    echo "usage: $0 snapshot|reset|top|calls|plans|io [N]" >&2
    exit 2
    ;;
esac
