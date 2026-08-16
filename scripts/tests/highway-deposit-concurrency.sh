#!/usr/bin/env bash
# Reproduce — or fail to reproduce — AB/BA deadlocks between concurrent
# consensus.highway_mask_deposit callers, and report the throughput that costs.
#
#   highway-deposit-concurrency.sh [database] [workers] [chunks] [overlap_pct]
#
# WHY THIS EXISTS. highway_mask_deposit.sql.in:11-35 is a ledger of THREE attempts at
# concurrent deposit, each falsified only by a production ingest failing:
#
#   bd10fcf    concurrent server-side deposits -> AB/BA deadlocks on shared entity rows
#              (hot words appear in every delta); 10/10 whole-batch retries lost.
#   #729       ordered the locks WITHIN one statement -> still deadlocked, because the
#              transaction runs a SEQUENCE of chunk statements holding prior chunks' locks.
#   #732/#733  sorted pairs client-side, committed per chunk -> deadlock gone, replaced by
#              a measured 6.4 ms/row FOR NO KEY UPDATE tax (EXPLAIN ANALYZE: 84 ms to
#              find+sort 5,000 rows, 32,039 ms in LockRows) as concurrent lockers minted
#              multixacts per tuple. Fold masks/s 3,305 (crashing) -> 333 (correct).
#
# The surviving shape is one global advisory lock, and it is a DELIBERATE trade: measured
# 2026-08-16, 45.4% of all deposit time is spent acquiring it (2.581 of 5.683 cumulative
# hours across 4,349 calls), with ~2.1 backends parked in that queue at any instant.
#
# So the convoy is known and priced. What is missing is a way to price a FOURTH attempt
# without discovering the answer during a 2h37m corpus ingest. Every prior attempt was
# tested by shipping it. This is the instrument that makes the next one falsifiable
# beforehand -- it does not propose a fix, it makes one checkable.
#
# WHAT IT MEASURES
#   deadlocks      count of SQLSTATE 40P01 across all workers (the failure mode)
#   serialization  count of 40001
#   wall           total seconds for the whole fan-out
#   deposits/s     completed deposit calls per second
#   lock_wait_pct  share of deposit time spent in pg_advisory_xact_lock, from
#                  pg_stat_statements deltas taken around the run
#
# OVERLAP is the variable that matters: AB/BA needs two transactions to touch the same
# entity rows in opposite orders. overlap_pct 0 means disjoint sets (no cycle possible);
# 100 means every worker touches the same rows (maximum contention). The 2026-07-29
# incident was driven by hot words appearing in every delta, i.e. high overlap.
set -euo pipefail

DB="${1:-${PGDATABASE:-laplace}}"
WORKERS="${2:-8}"
CHUNKS="${3:-20}"
OVERLAP="${4:-60}"
ENTITIES_PER_CHUNK="${LAPLACE_DEPOSIT_CHUNK:-500}"
# The relation whose bit is deposited. Must be one the target entities do NOT already
# carry, or every UPDATE is filtered and the test measures nothing.
DEP_TYPE="${LAPLACE_DEPOSIT_TYPE:-IS_A}"

PSQL=(psql -h "${PGHOST:-/var/run/postgresql}" -U "${PGUSER:-laplace_admin}" -d "$DB" -v ON_ERROR_STOP=1 -qAt)

# REFUSE ON A LIVE SUBSTRATE. This writes entity rows; running it under an ingest would
# both corrupt the measurement and contend with real work. Same signal MeasurementLane
# uses: a counter that advances, not a beacon that can be absent under a live run.
d0=$("${PSQL[@]}" -c "SELECT coalesce(sum(input_units_done),0) FROM laplace.ingest_run_journal WHERE status='running';" 2>/dev/null || echo "?")
sleep 2
d1=$("${PSQL[@]}" -c "SELECT coalesce(sum(input_units_done),0) FROM laplace.ingest_run_journal WHERE status='running';" 2>/dev/null || echo "?")
if [[ ! "$d0" =~ ^[0-9]+$ || ! "$d1" =~ ^[0-9]+$ ]]; then
  echo "::error::cannot establish ingest state on '$DB' — refusing (an unanswerable probe is not proof of quiet)" >&2
  exit 1
fi
if [ "$d1" -gt "$d0" ]; then
  echo "::error::ingest ADVANCING on '$DB' ($d0 -> $d1) — refusing to run a write-path stress test over it" >&2
  exit 1
fi

"${PSQL[@]}" -c "SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                  WHERE n.nspname='consensus' AND p.proname='highway_mask_deposit';" | grep -q 1 || {
  echo "::error::consensus.highway_mask_deposit not installed in '$DB'" >&2; exit 1; }

echo "database=$DB workers=$WORKERS chunks=$CHUNKS overlap=${OVERLAP}% entities/chunk=$ENTITIES_PER_CHUNK"

# A fixed pool of real entity ids. SHARED holds the overlap; each worker also draws
# private ids so the sets are not identical (identical sets serialize trivially and never
# produce the opposite-order interleave a cycle needs).
# NON-DESTRUCTIVE BY CONSTRUCTION. highway_mask is a read-side authority: a bit set there
# claims the entity participates in that relation, so depositing a bit the entity does not
# legitimately carry is data corruption, not a harmless benchmark. But depositing a bit it
# ALREADY has updates nothing (`NOT (bits @> d.bits)` filters it), which measures nothing.
# The only way to have both is to snapshot the masks this run can touch and put them back.
"${PSQL[@]}" -c "
  DROP TABLE IF EXISTS laplace._dep_pool;
  CREATE TABLE laplace._dep_pool AS
    SELECT e.id, e.highway_mask AS mask0, row_number() OVER (ORDER BY e.id) AS n
    FROM laplace.entities e LIMIT $(( ENTITIES_PER_CHUNK * (WORKERS + 2) ));
  SELECT count(*) FROM laplace._dep_pool;" > /dev/null

restore_masks() {
  "${PSQL[@]}" -c "
    UPDATE laplace.entities e SET highway_mask = p.mask0
      FROM laplace._dep_pool p
     WHERE e.id = p.id AND e.highway_mask IS DISTINCT FROM p.mask0;" >/dev/null 2>&1 || true
}
# Restore on ANY exit, including the refusal paths and Ctrl-C. A stress test that leaves
# forged bits behind is worse than no stress test.
trap 'restore_masks; "${PSQL[@]}" -c "DROP TABLE IF EXISTS laplace._dep_pool;" >/dev/null 2>&1 || true; rm -rf "${tmp:-}"' EXIT

before=$("${PSQL[@]}" -c "
  SELECT coalesce(sum(total_exec_time) FILTER (WHERE query ILIKE '%highway_mask_deposit%'),0)::bigint
      || ' ' || coalesce(sum(total_exec_time) FILTER (WHERE query ILIKE '%pg_advisory_xact_lock%'),0)::bigint
  FROM laplace.pg_stat_statements;" 2>/dev/null || echo "0 0")

tmp=$(mktemp -d)
start=$SECONDS

for w in $(seq 1 "$WORKERS"); do
(
  for _ in $(seq 1 "$CHUNKS"); do
    # Each chunk is its own transaction, mirroring ConsensusAccumulatingWriter's
    # commit-per-chunk. ORDER BY random() is deliberate: it is what makes two workers
    # touch shared rows in OPPOSITE orders, which is the precondition for AB/BA.
    "${PSQL[@]}" -c "
      BEGIN;
      WITH picked AS (
        SELECT id FROM laplace._dep_pool
         WHERE n <= $ENTITIES_PER_CHUNK * $OVERLAP / 100
            OR n BETWEEN $ENTITIES_PER_CHUNK * ($w - 1) + 1 AND $ENTITIES_PER_CHUNK * $w
         ORDER BY random() LIMIT $ENTITIES_PER_CHUNK
      )
      SELECT consensus.highway_mask_deposit(
               array_agg(id),
               array_agg(laplace.relation_type_id('$DEP_TYPE')))
      FROM picked;
      COMMIT;" >>"$tmp/w$w.out" 2>>"$tmp/w$w.err" || echo "FAIL" >> "$tmp/w$w.err"
  done
) &
done
wait
wall=$(( SECONDS - start ))

after=$("${PSQL[@]}" -c "
  SELECT coalesce(sum(total_exec_time) FILTER (WHERE query ILIKE '%highway_mask_deposit%'),0)::bigint
      || ' ' || coalesce(sum(total_exec_time) FILTER (WHERE query ILIKE '%pg_advisory_xact_lock%'),0)::bigint
  FROM laplace.pg_stat_statements;" 2>/dev/null || echo "0 0")

deadlocks=$(cat "$tmp"/*.err 2>/dev/null | grep -c '40P01\|deadlock detected' || true)
serial=$(cat "$tmp"/*.err 2>/dev/null | grep -c '40001' || true)
fails=$(cat "$tmp"/*.err 2>/dev/null | grep -c 'FAIL' || true)
total=$(( WORKERS * CHUNKS ))

dep_b=${before% *}; lock_b=${before#* }
dep_a=${after% *};  lock_a=${after#* }
dep_d=$(( dep_a - dep_b )); lock_d=$(( lock_a - lock_b ))
pct=0; [ "$dep_d" -gt 0 ] && pct=$(( 100 * lock_d / dep_d ))

echo
echo "deposits attempted : $total"
echo "failed             : $fails"
echo "deadlocks (40P01)  : $deadlocks"
echo "serialization 40001: $serial"
echo "wall seconds       : $wall"
[ "$wall" -gt 0 ] && echo "deposits/s         : $(( total / wall ))"
echo "deposit ms (delta) : $(( dep_d / 1000 ))"
echo "lock ms (delta)    : $(( lock_d / 1000 ))"
echo "lock_wait_pct      : ${pct}%"

# ROWS ACTUALLY UPDATED. A deposit whose bits are already present updates NOTHING --
# `NOT (highway_mask_bits(e.highway_mask) @> d.bits)` filters it out -- so on a seeded
# substrate the default IS_A deposit is a no-op and the run touches no entity row. Zero
# deadlocks after zero work is not evidence of anything, and reporting it as a pass is
# how an instrument lies. Fail loudly instead.
rows=$(cat "$tmp"/*.out 2>/dev/null | awk '/^[0-9]+$/ {s+=$1} END {print s+0}')
echo "entity rows updated: $rows"
if [ "$rows" -eq 0 ]; then
  echo "::error::the run updated ZERO entity rows — every deposit was filtered as already-present, so no row contention existed and the deadlock result proves NOTHING. Use a relation whose bit is absent (LAPLACE_DEPOSIT_TYPE) or a database whose entities lack it." >&2
  exit 2
fi

# verify the restore actually put every mask back before declaring anything
drift=$("${PSQL[@]}" -c "
  SELECT count(*) FROM laplace.entities e JOIN laplace._dep_pool p ON p.id = e.id
   WHERE e.highway_mask IS DISTINCT FROM p.mask0;" 2>/dev/null || echo "?")
restore_masks
drift_after=$("${PSQL[@]}" -c "
  SELECT count(*) FROM laplace.entities e JOIN laplace._dep_pool p ON p.id = e.id
   WHERE e.highway_mask IS DISTINCT FROM p.mask0;" 2>/dev/null || echo "?")
echo "masks changed / left changed: ${drift} / ${drift_after}"
[ "$drift_after" = "0" ] || { echo "::error::masks NOT fully restored — ${drift_after} rows still differ" >&2; exit 3; }

[ "$deadlocks" -eq 0 ] && [ "$fails" -eq 0 ]
