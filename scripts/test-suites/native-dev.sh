#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=scripts/test-suites/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_native_dev() {
  run_ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -LE regress
}

run_native_dev
