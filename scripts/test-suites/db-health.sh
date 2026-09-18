#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=scripts/test-suites/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_db_health() {
  bash scripts/check-database-health.sh "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
}

run_db_health
