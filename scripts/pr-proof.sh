#!/usr/bin/env bash
# Same-repository pull-request proof. Source/build only: no install, migration,
# extension synchronization, service control, database mutation, or publication.
# Runtime compatibility with /opt/laplace is intentionally proved only after the
# exact revision has been installed by the main delivery lane.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

bash scripts/ci-policy.sh

bash scripts/pipeline.sh build
bash scripts/test-parallel.sh --engine --all

runtime=$(mktemp -d)
trap 'rm -rf "$runtime"' EXIT
dotnet publish app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj \
  -c Release --no-self-contained -o "$runtime"
python3 scripts/test-uci-publish.py "$runtime"
python3 scripts/install-stockfish.py --prefix "$runtime/chess-tools"
python3 scripts/test-cutechess-runtime.py /opt/laplace/bin/cutechess-cli \
  "$runtime/laplace-uci" "$runtime/chess-tools/bin/stockfish"

(
  cd web
  npm ci --no-audit --no-fund
  npm run typecheck
  npx playwright install chromium
  npm run test:chess-ui
)

echo "PR_PROOF_OK policy=green build=green dev_bat=green ui=green production_mutations=0"
