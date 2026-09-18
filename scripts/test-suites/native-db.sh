#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_native_db() {
  set_installed_perfcache
  run_ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -L regress
}

run_native_db
