#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=scripts/test-suites/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_dev() {
  set_dev_perfcache
  sync_managed_native
  python3 scripts/test-managed-policy.py
  python3 scripts/test-application-payload.py
  python3 scripts/test-cutechess-calibration.py
  python3 scripts/test-chess-x11-runtime.py
  python3 scripts/test-chess-floor-artifacts.py
  python3 scripts/test-recorded-chess-selection.py
  python3 scripts/test-chess-environment-benchmark.py ChessEnvironmentTests
  python3 scripts/test-ci-workspace.py
  python3 scripts/test-product-ci-artifact-ownership.py
  python3 scripts/test-seed-workflow-ownership.py
  python3 scripts/test-managed-db-scheduling.py
  python3 scripts/test-codegen-configure.py
  python3 scripts/test-cmake-release.py
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal \
    --filter 'Tier!=db&Tier!=live&Tier!=perf'
}

run_managed_dev
