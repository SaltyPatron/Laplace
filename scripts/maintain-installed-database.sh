#!/usr/bin/env bash
# Non-destructive maintenance of an already-installed Laplace database.
# Destructive recreation and corpus restoration are explicit operator operations.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
mode="${1:-all}"
case "$mode" in
  all|--prepare) ;;
  *) echo "usage: maintain-installed-database.sh [--prepare]" >&2; exit 2 ;;
esac
[[ "$#" -le 1 ]] || { echo "unexpected database maintenance arguments" >&2; exit 2; }

database="${PGDATABASE:-laplace}"
user="${PGUSER:-laplace_admin}"

# Maintenance must never silently turn a developer database into a destructive
# migration/reseed campaign. Classify storage once and fail before mutation if an
# explicit recreate is required. The DB operator workflow owns that decision.
if [[ "${LAPLACE_FRESH_DB:-}" != 1 ]]; then
  if ! entity_storage_generation="$(psql -X -d "$database" -U "$user" -tAX -v ON_ERROR_STOP=1 <<'SQL'
SELECT CASE
         WHEN e.oid IS NULL THEN 'absent'
         WHEN e.relkind = 'p' AND p.partstrat = 'h'
              AND i.relkind IN ('r', 'p') THEN 'canonical'
         WHEN e.relkind = 'r'
              OR (e.relkind = 'p' AND p.partstrat IN ('l', 'r'))
              OR (e.relkind = 'p' AND p.partstrat = 'h' AND i.oid IS NULL)
           THEN 'incompatible'
         ELSE 'unsupported'
       END
FROM (SELECT pg_catalog.to_regclass('laplace.entities') AS entity_id,
             pg_catalog.to_regclass('laplace.entity_interpretations') AS interpretation_id) AS selected
LEFT JOIN pg_catalog.pg_class AS e ON e.oid = selected.entity_id
LEFT JOIN pg_catalog.pg_partitioned_table AS p ON p.partrelid = e.oid
LEFT JOIN pg_catalog.pg_class AS i ON i.oid = selected.interpretation_id;
SQL
  )"; then
    echo "::error::could not classify entity storage in $database; maintenance was not started" >&2
    exit 1
  fi

  case "$entity_storage_generation" in
    absent|canonical) ;;
    incompatible)
      echo "::error::$database uses an incompatible entity-storage generation" >&2
      echo "::error::recreate is destructive and must be requested explicitly through DB — lifecycle ops" >&2
      exit 2
      ;;
    *)
      echo "::error::unsupported entity-storage generation in $database; maintenance was not started" >&2
      exit 2
      ;;
  esac
fi

args=()
[[ "${LAPLACE_FRESH_DB:-}" != 1 ]] || args+=(--fresh-db)
bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env

if [[ "$mode" == all ]]; then
  bash scripts/reconcile-highway-masks.sh "$database"
fi
bash scripts/check-database-health.sh "$database"
