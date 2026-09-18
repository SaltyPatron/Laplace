#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=scripts/test-suites/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_dev() {
  set_dev_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_TEST_PROJECTS:-all}" managed-dev \
    'Tier!=db&Tier!=live&Tier!=perf'
}

run_managed_dev
