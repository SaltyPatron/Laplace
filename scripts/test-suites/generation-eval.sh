#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_generation_eval() {
  mkdir -p "$ROOT/build/eval-proof"
  python3 scripts/eval-generation.py --api "${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}" \
    --probes scripts/eval-probes.json --baseline scripts/eval-baselines.json --report "$ROOT/build/eval-proof/generation.json"
}

run_generation_eval
