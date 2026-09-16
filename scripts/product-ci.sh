#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-build}"
case "$stage" in
  provision|reconcile|check|build|install|database|applications|deploy|test-dev|test-db|test-live) ;;
  *) echo "unknown product stage: $stage" >&2; exit 2 ;;
esac

check_deps() {
  bash scripts/ci-deps.sh --check-only
}

provision_deps() {
  bash scripts/ci-deps.sh
}

run_build() {
  local args=()
  [[ "${LAPLACE_FULL_CLEAN:-}" != 1 ]] || args+=(--force-rebuild)
  [[ "${LAPLACE_FORCE_CODEGEN:-}" != 1 ]] || args+=(--force-codegen)
  bash scripts/pipeline.sh "${args[@]}" build
  mkdir -p build
  git rev-parse HEAD > build/.laplace-source-revision
}

run_dev_tests() {
  bash scripts/test-parallel.sh --profile dev-native --suite native-dev
  bash scripts/test-parallel.sh --profile dev-managed --suite managed-dev
  bash scripts/test-parallel.sh --profile dev-managed --suite uci-dev
  bash scripts/test-parallel.sh --profile dev-managed --suite browser-dev
}

run_install() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}" 60
  bash scripts/pipeline.sh install
}

run_database_maintenance() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}" 60
  bash scripts/maintain-installed-database.sh
}

run_db_tests() {
  bash scripts/test-parallel.sh --profile db --suite db-health
  rm -rf build/extension/*/tests/regress_output
  bash scripts/test-parallel.sh --profile db --suite native-db
  bash scripts/test-parallel.sh --profile db --suite managed-db
}

run_publish() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}" 60
  bash scripts/publish-applications.sh deploy
}

run_live_tests() {
  export LAPLACE_API_BASE="${LAPLACE_API_BASE:-http://127.0.0.1:8080}"
  bash scripts/test-parallel.sh --profile live --suite live-floor
  bash scripts/test-parallel.sh --profile live --suite live-api
  bash scripts/test-parallel.sh --profile live --suite managed-live
  bash scripts/test-parallel.sh --profile live --suite generation-eval
}

reconcile_installed_product() {
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  curl -fsS http://127.0.0.1:5187/health/ready | grep -q '"ready":true'
}

case "$stage" in
  provision)
    provision_deps
    ;;
  check)
    bash -n scripts/product-ci.sh scripts/pipeline.sh scripts/ci-deps.sh scripts/test-parallel.sh
    ;;
  reconcile)
    reconcile_installed_product
    ;;
  build)
    check_deps
    run_build
    ;;
  install)
    run_install
    ;;
  database)
    run_database_maintenance
    ;;
  applications)
    run_publish
    ;;
  test-dev)
    run_dev_tests
    ;;
  test-db)
    run_db_tests
    ;;
  test-live)
    run_live_tests
    ;;
  deploy)
    check_deps
    run_build
    run_install
    run_database_maintenance
    run_publish
    ;;
esac
