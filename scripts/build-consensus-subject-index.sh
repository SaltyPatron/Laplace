#!/usr/bin/env bash
# Build consensus_subject — consensus (subject_id) INCLUDE (object_id, rating, rd) —
# without the AccessExclusiveLock that stops it on a populated cluster.
#
# CREATE INDEX against the partitioned parent (schema/tables/consensus.sql.in) takes
# AccessExclusive on the whole tree and queues behind any running ingest, which is why
# the index has never actually been built on this cluster. CREATE INDEX CONCURRENTLY
# is not valid on a partitioned parent and is not valid inside a transaction block, so
# it cannot live in a migration either. The supported route is: build each LEAF
# concurrently (ShareUpdateExclusive, coexists with INSERT), create the parent index
# ON ONLY (catalog-only, instant), then ATTACH each leaf.
#
# Idempotent: IF NOT EXISTS on every leaf, ATTACH skips what is already attached.
# Re-run after an interrupted pass; it resumes and reports any INVALID leftovers.
#
# Usage: scripts/build-consensus-subject-index.sh [dbname]
set -uo pipefail

DB="${1:-${PGDATABASE:-laplace}}"
PSQL=(psql -h "${PGHOST:-/var/run/postgresql}" -U "${PGUSER:-laplace_admin}" -d "$DB" -tA)

# Stop before the WAL volume fills rather than tune a constant after it does. The
# guard is a fraction of the volume, not a magic number: concurrent builds on a 29 GB
# heap emit WAL on the order of the index they produce, alongside whatever else writes.
WAL_DIR=$("${PSQL[@]}" -c "SELECT setting FROM pg_settings WHERE name='data_directory'")/pg_wal
[ -d "$WAL_DIR" ] || WAL_DIR=$(readlink -f "$WAL_DIR" 2>/dev/null || echo /var/lib/pgwal)
WAL_TOTAL_GB=$(df -BG --output=size "$WAL_DIR" 2>/dev/null | tail -1 | tr -dc '0-9')
WAL_MIN_GB=$(( ${WAL_TOTAL_GB:-64} / 8 ))

log() { echo "[$(date -u +%H:%M:%S)] $*"; }
wal_free_gb() { df -BG --output=avail "$WAL_DIR" 2>/dev/null | tail -1 | tr -dc '0-9'; }

mapfile -t LEAVES < <("${PSQL[@]}" -F'|' <<'SQL'
WITH RECURSIVE t AS (
  SELECT c.oid, c.relname, c.relkind FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE n.nspname = 'laplace' AND c.relname = 'consensus'
  UNION ALL
  SELECT c.oid, c.relname, c.relkind FROM t
    JOIN pg_inherits i ON i.inhparent = t.oid
    JOIN pg_class c ON c.oid = i.inhrelid
)
SELECT relname FROM t WHERE relkind = 'r' ORDER BY pg_relation_size(oid) DESC;
SQL
)

total=${#LEAVES[@]}
[ "$total" -gt 0 ] || { echo "no consensus leaves found in $DB" >&2; exit 2; }
log "$total leaves; WAL $WAL_DIR free $(wal_free_gb)G of ${WAL_TOTAL_GB}G, guard ${WAL_MIN_GB}G"

built=0
for i in "${!LEAVES[@]}"; do
  leaf="${LEAVES[$i]}"
  [ -n "$leaf" ] || continue
  free=$(wal_free_gb)
  if [ "${free:-0}" -lt "$WAL_MIN_GB" ]; then
    log "STOP at $((i+1))/$total: WAL free ${free}G under ${WAL_MIN_GB}G guard; $built built. Re-run to resume."
    exit 3
  fi
  t0=$SECONDS
  if out=$("${PSQL[@]}" -c "CREATE INDEX CONCURRENTLY IF NOT EXISTS ${leaf}_subject ON laplace.${leaf} (subject_id) INCLUDE (object_id, rating, rd);" 2>&1); then
    built=$((built+1))
    log "ok   $((i+1))/$total $leaf $((SECONDS-t0))s wal_free=${free}G"
  else
    log "FAIL $((i+1))/$total $leaf $((SECONDS-t0))s: $out"
  fi
done

log "leaves built this pass: $built/$total"
log "invalid leftovers: $("${PSQL[@]}" -c "SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid WHERE c.relname ~ '_subject\$' AND NOT i.indisvalid;")"

log "parent index ON ONLY (catalog-only)"
"${PSQL[@]}" -c "CREATE INDEX IF NOT EXISTS consensus_subject ON ONLY laplace.consensus (subject_id) INCLUDE (object_id, rating, rd);"

log "attaching leaves"
"${PSQL[@]}" -c "
DO \$\$
DECLARE r record; n int := 0;
BEGIN
  FOR r IN
    SELECT ic.relname AS idx
      FROM pg_class c
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'laplace'
      JOIN pg_class ic ON ic.relname = c.relname || '_subject'
      JOIN pg_index i ON i.indexrelid = ic.oid AND i.indisvalid
     WHERE c.relkind = 'r' AND c.relname LIKE 'consensus\_%'
  LOOP
    BEGIN
      EXECUTE format('ALTER INDEX laplace.consensus_subject ATTACH PARTITION laplace.%I', r.idx);
      n := n + 1;
    EXCEPTION WHEN others THEN
      RAISE NOTICE 'attach % failed: %', r.idx, SQLERRM;
    END;
  END LOOP;
  RAISE NOTICE 'attached %', n;
END \$\$;"

valid=$("${PSQL[@]}" -c "SELECT indisvalid FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid WHERE c.relname='consensus_subject';")
log "parent index valid: ${valid:-absent}"
log "index total: $("${PSQL[@]}" -c "SELECT pg_size_pretty(COALESCE(sum(pg_relation_size(indexrelid)),0)) FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid WHERE c.relname ~ '_subject\$';")"
[ "$valid" = "t" ] || exit 4
