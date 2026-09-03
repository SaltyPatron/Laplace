#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

python3 scripts/test-dataset-estate-refresh.py
bash -n scripts/dataset-estate-refresh.sh

# Help/plan are source-only and must not touch active data. Run them against a
# temporary refresh root so the operator's argument parser is exercised in CI.
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
LAPLACE_DATA_ROOT="$tmp/data" \
LAPLACE_DATA_REFRESH_ROOT="$tmp/refresh" \
  scripts/dataset-estate-refresh.sh plan semantic >/dev/null
LAPLACE_DATA_ROOT="$tmp/data" \
LAPLACE_DATA_REFRESH_ROOT="$tmp/refresh" \
  scripts/dataset-estate-refresh.sh status >/dev/null

echo "DATASET_ESTATE_REFRESH_SHELL_OK"
