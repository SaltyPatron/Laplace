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

# Canonical entity identity changed from LIST(tier)/(id,tier) to HASH(id)/(id),
# with tier/type multiplicity retained in entity_interpretations. That storage
# generation is deliberately greenfield: trying to run the ordinary extension
# upgrade against the old physical layout leaves a mixed schema and fails after
# installation. Detect the incompatible installed generation before migration
# and take the repository's declared reset+reseed path exactly once. A database
# already on the canonical generation is never reset by this check.
needs_identity_reseed=0
if [[ "${LAPLACE_FRESH_DB:-}" != 1 ]]; then
  if psql -d "${PGDATABASE:-laplace}" -U "${PGUSER:-laplace_admin}" -tAX -v ON_ERROR_STOP=1 \
      -c "SELECT to_regclass('laplace.entities') IS NOT NULL" 2>/dev/null | grep -qx t; then
    entity_partition_strategy="$(psql -d "${PGDATABASE:-laplace}" -U "${PGUSER:-laplace_admin}" -tAX -v ON_ERROR_STOP=1 -c \
      "SELECT p.partstrat FROM pg_partitioned_table p WHERE p.partrelid='laplace.entities'::regclass" 2>/dev/null || true)"
    have_interpretations="$(psql -d "${PGDATABASE:-laplace}" -U "${PGUSER:-laplace_admin}" -tAX -v ON_ERROR_STOP=1 -c \
      "SELECT to_regclass('laplace.entity_interpretations') IS NOT NULL" 2>/dev/null || true)"
    if [[ "$entity_partition_strategy" != h || "$have_interpretations" != t ]]; then
      needs_identity_reseed=1
      echo "::notice::installed substrate uses the pre-canonical entity storage generation; recreating and reseeding before activation"
    fi
  fi
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
