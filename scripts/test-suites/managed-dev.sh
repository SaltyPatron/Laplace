#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_dev() (
  # TestServer instances own their key ring; they cannot borrow the serving
  # process's protected directory or mutate its browser-session keys.
  local state
  state=$(mktemp -d "$TMPDIR/managed-dev-XXXXXXXX")
  trap 'rm -rf -- "$state"' EXIT
  export LAPLACE_DATA_PROTECTION_KEYS="$state/keys"
  set_dev_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_TEST_PROJECTS:-all}" managed-dev \
    'Tier!=db&Tier!=live&Tier!=perf'
)

run_managed_dev
