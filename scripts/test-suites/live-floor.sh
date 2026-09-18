#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_live_floor() {
  set_installed_perfcache
  bash scripts/check-substrate-floor.sh "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
}

run_live_floor
