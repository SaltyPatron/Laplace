#!/usr/bin/env bash
set -euo pipefail

DB="${1:-${PGDATABASE:-laplace}}"
BATCH="${LAPLACE_HIGHWAY_RECONCILE_BATCH:-500000}"

psql_scalar() {
  psql -d "$DB" -U laplace_admin -v ON_ERROR_STOP=1 -Atc "$1"
}

have=$(psql_scalar "SELECT to_regclass('laplace.highway_mask_pending') IS NOT NULL AND to_regclass('laplace.highway_mask_population_state') IS NOT NULL AND to_regprocedure('laplace.highway_mask_pending_drain(integer)') IS NOT NULL AND to_regprocedure('laplace.highway_mask_rebuild(integer)') IS NOT NULL")
if [[ "$have" != t ]]; then
  echo "highway masks: reconciliation surface not installed yet"
  exit 0
fi

ready=$(psql_scalar "SELECT consensus.highway_ready()")
if [[ "$ready" != t ]]; then
  # Do not block writes or throw work away. The deposit wrapper durably queues the
  # exact (entity,type) pairs until the registry becomes available.
  pending=$(psql_scalar "SELECT count(*) FROM laplace.highway_mask_pending")
  echo "highway masks: registry unavailable; $pending exact pairs retained for replay"
  exit 0
fi

complete=$(psql_scalar "SELECT complete FROM laplace.highway_mask_population_state WHERE singleton")
if [[ "$complete" != t ]]; then
  echo "highway masks: reconciling pre-deposit consensus estate"
  psql -d "$DB" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "CALL laplace.highway_mask_rebuild($BATCH)"
else
  psql -d "$DB" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "CALL laplace.highway_mask_pending_drain($BATCH)"
fi

pending=$(psql_scalar "SELECT count(*) FROM laplace.highway_mask_pending")
masked=$(psql_scalar "SELECT count(*) FROM laplace.entities WHERE highway_mask IS NOT NULL")
echo "highway masks: $masked entities populated; $pending exact pairs pending"
