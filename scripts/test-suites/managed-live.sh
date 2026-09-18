#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=scripts/test-suites/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_live() {
  set_installed_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_LIVE_TEST_PROJECTS:-all}" managed-live 'Tier=live'
}

run_managed_live
