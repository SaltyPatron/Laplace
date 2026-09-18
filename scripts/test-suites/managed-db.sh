#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_db() {
  set_installed_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_DB_TEST_PROJECTS:-all}" managed-db \
    'Tier=db' -m:1 -p:BuildInParallel=false
}

run_managed_db
