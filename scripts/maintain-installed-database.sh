#!/usr/bin/env bash
# Prepare installed schema/settings; full database maintenance also reconciles data.
# The caller holds the host reservation; this script does not stop database writers.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
mode="${1:-all}"
case "$mode" in
  all|--prepare) ;;
  *) echo "usage: maintain-installed-database.sh [--prepare]" >&2; exit 2 ;;
esac
[[ "$#" -le 1 ]] || { echo "unexpected database maintenance arguments" >&2; exit 2; }

# Canonical identity uses HASH(id) plus plural interpretation rows. Classify
# the selected database from one successful catalog snapshot before requesting
# the existing reset path. A failed or incomplete probe is never reset evidence.
needs_identity_reseed=0
if [[ "${LAPLACE_FRESH_DB:-}" != 1 ]]; then
  if ! entity_storage_generation="$(psql -X -d "${PGDATABASE:-laplace}" -U "${PGUSER:-laplace_admin}" -tAX -v ON_ERROR_STOP=1 <<'SQL'
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
    echo "::error::could not classify entity storage in ${PGDATABASE:-laplace}; database maintenance was not started" >&2
    exit 1
  fi
  case "$entity_storage_generation" in
    absent|canonical) ;;
    incompatible)
      needs_identity_reseed=1
      echo "::notice::selected database ${PGDATABASE:-laplace} uses incompatible entity storage; recreating it before extension activation"
      ;;
    *)
      echo "::error::unrecognized entity storage classification; database maintenance was not started" >&2
      exit 1
      ;;
  esac
fi

args=()
if [[ "${LAPLACE_FRESH_DB:-}" == 1 || "$needs_identity_reseed" == 1 ]]; then
  args+=(--fresh-db)
fi
bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env
# The identity-storage contract explicitly makes reset+reseed the migration path.
# Leaving the recreated canonical schema empty would make the same lifecycle fail
# its live substrate floor later and, more importantly, would not restore a usable
# Laplace installation. Re-admit the complete canonical foundation through the
# normal generic ingest spine using the product runtime that the caller already built.
if [[ "$needs_identity_reseed" == 1 ]]; then
  bash scripts/ensure-foundation.sh
fi

if [[ "$mode" == all ]]; then
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
fi
bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
