#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_perf() {
  set_installed_perfcache
  sync_managed_native
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal --filter 'Tier=perf'
  mkdir -p "$ROOT/build/eval-proof"
  python3 scripts/verify-generation.py --api "${LAPLACE_API_BASE:-http://127.0.0.1:8080}" \
    --report "$ROOT/build/eval-proof/lane-detectors.json" --enforce
}

run_perf
