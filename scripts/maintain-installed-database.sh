#!/usr/bin/env bash
# Called by product-ci inside the managed writer-quiescence transaction.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
args=()
[[ "${LAPLACE_FRESH_DB:-}" != 1 ]] || args+=(--fresh-db)
bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env
# Repair runs after the corrected managed application generation is published.
bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
