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
args=()
[[ "${LAPLACE_FRESH_DB:-}" != 1 ]] || args+=(--fresh-db)
bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env
if [[ "$mode" == all ]]; then
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
fi
bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
