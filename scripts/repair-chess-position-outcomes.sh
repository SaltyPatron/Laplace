#!/usr/bin/env bash
# Existing product maintenance owns managed writer quiescence before this command.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
bash scripts/sync-managed-native-artifacts.sh
cli="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"
if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
  cli="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Cli/Release/net10.0/Laplace.Cli.dll"
fi
# Replace the owned shell with the actual producer. A wrapper timeout must kill
# that process, not leave a dotnet-run/MSBuild launcher able to start it later.
exec dotnet "$cli" \
  chess repair-position-outcomes \
  --evidence-root /build/laplace/recovery/chess-position-outcomes \
  --invocation "${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}" \
  --maximum-retained-mib "${LAPLACE_CHESS_OBSERVATION_RETAINED_MIB:-512}"
