#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_dev() (
  # TestServer instances own their key ring and chess artifacts; tests cannot
  # borrow the serving process's directories or mutate its retained state.
  local state
  state=$(mktemp -d "$TMPDIR/managed-dev-XXXXXXXX")
  trap 'rm -rf -- "$state"' EXIT
  export LAPLACE_DATA_PROTECTION_KEYS="$state/keys"
  export LAPLACE_CHESS_LAB_DIR="$state/chess-lab"
  set_dev_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_TEST_PROJECTS:-all}" managed-dev \
    'Category!=RequiresDatabase&Category!=RequiresNetwork&Category!=RequiresQualification'
)

run_managed_dev
