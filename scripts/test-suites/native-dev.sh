#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_native_dev() {
  local args=(--test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -LE regress)
  [[ -z "${LAPLACE_NATIVE_TEST_FILTER:-}" ]] || args+=(-R "$LAPLACE_NATIVE_TEST_FILTER")
  run_ctest "${args[@]}"
}

run_native_dev
