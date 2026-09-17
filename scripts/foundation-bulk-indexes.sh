#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PGHOST="${PGHOST:-/var/run/postgresql}"
PGUSER="${PGUSER:-laplace_admin}"
DB="${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
LAPLACE="$ROOT/scripts/laplace"

usage() {
  echo "Usage: $0 begin|end" >&2
  exit 2
}

recover() {
  env -u LAPLACE_INDEX_RECOVERY_DEFER "$LAPLACE" recover-indexes
}

case "${1:-}" in
  begin)
    # Never layer a new bulk campaign over an interrupted old one. Recover first;
    # after the drop commits, child ingests deliberately defer this same recovery
    # until the campaign finishes.
    recover
    psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -X <<'SQL'
BEGIN;
SET LOCAL lock_timeout = '5s';
DO $laplace$
DECLARE
  r record;
BEGIN
  FOR r IN
    WITH RECURSIVE parts(table_name, oid) AS (
      SELECT v.table_name, pg_catalog.to_regclass(pg_catalog.format('laplace.%I', v.table_name))
      FROM (VALUES ('entities'), ('physicalities'), ('attestations'), ('consensus')) AS v(table_name)
      UNION ALL
      SELECT p.table_name, i.inhrelid
      FROM pg_catalog.pg_inherits i
      JOIN parts p ON i.inhparent = p.oid
    )
    SELECT DISTINCT
      p.table_name,
      c.relname AS index_name,
      replace(pg_catalog.pg_get_indexdef(i.indexrelid), ' ON ONLY ', ' ON ') AS index_def
    FROM parts p
    JOIN pg_catalog.pg_index i ON i.indrelid = p.oid
    JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid
    WHERE p.oid IS NOT NULL
      AND NOT i.indisprimary
      AND NOT i.indisunique
      AND NOT i.indisexclusion
      AND NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_inherits ih
        WHERE ih.inhrelid = i.indexrelid
      )
    ORDER BY p.table_name, c.relname
  LOOP
    INSERT INTO laplace.index_cycle_journal (index_name, table_name, index_def)
    VALUES (r.index_name, r.table_name, r.index_def)
    ON CONFLICT (index_name) DO UPDATE
      SET table_name = EXCLUDED.table_name,
          index_def = EXCLUDED.index_def;

    EXECUTE pg_catalog.format('DROP INDEX %I.%I', 'laplace', r.index_name);
  END LOOP;
END
$laplace$;
COMMIT;
SQL
    pending="$(psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -X -tAc \
      "SELECT count(*) FROM laplace.index_cycle_journal;" | xargs)"
    echo "FOUNDATION_BULK_INDEXES begin database=$DB journaled=$pending"
    ;;
  end)
    recover
    pending="$(psql -h "$PGHOST" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -X -tAc \
      "SELECT count(*) FROM laplace.index_cycle_journal;" | xargs)"
    [[ "$pending" == 0 ]] || {
      echo "foundation bulk-index recovery left $pending journal row(s) on $DB" >&2
      exit 1
    }
    echo "FOUNDATION_BULK_INDEXES end database=$DB journaled=0"
    ;;
  *) usage ;;
esac
