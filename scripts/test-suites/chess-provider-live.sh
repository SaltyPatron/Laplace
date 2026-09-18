#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

base="${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}"
output="${RUNNER_TEMP:-$ROOT/build}/chess-provider-live-proof.json"
python3 scripts/tests/chess-provider-live.py \
  --api-base "$base" \
  --user "${LAPLACE_CHESS_PROVIDER_PROOF_USER:-Anthony-Hart}" \
  --site "${LAPLACE_CHESS_PROVIDER_PROOF_SITE:-chesscom}" \
  --games "${LAPLACE_CHESS_PROVIDER_PROOF_GAMES:-0}" \
  --output "$output"
echo "REAL_CHESS_PROVIDER_PROOF=$output"
cat "$output"
