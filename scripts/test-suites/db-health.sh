#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_db_health() {
  if [[ "${LAPLACE_REUSE_INSTALLED_NATIVE:-0}" == 1 ]]; then
    bash scripts/check-database-health.sh --installed-runtime "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
  else
    bash scripts/check-database-health.sh "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
  fi
}

run_db_health
