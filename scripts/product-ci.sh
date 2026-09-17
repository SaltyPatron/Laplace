#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-build}"
case "$stage" in
  provision|reconcile|check|build|install|applications|deploy|mainline|test-dev|test-db|test-live) ;;
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
  if [[ "${LAPLACE_FORCE_CODEGEN:-}" == 1 || \
        ! -f extension/laplace_substrate/sql/generated/seed_relation_types.sql.in || \
        ! -f extension/laplace_substrate/sql/generated/seed_pos.sql.in ]]; then
    args+=(--force-codegen)
  fi
  bash scripts/pipeline.sh "${args[@]}" build
  mkdir -p build
  git rev-parse HEAD > build/.laplace-source-revision
}

run_dev_tests() {
  # Development tests are evidence, not a reason to discard the rest of the
  # integrated lifecycle. Run every suite, remember any failure, and return it
  # after the remaining suites have had a chance to report their own state.
  local rc=0
  bash scripts/test-parallel.sh --profile dev-native --suite native-dev || rc=$?
  bash scripts/test-parallel.sh --profile dev-managed --suite managed-dev || rc=$?
  bash scripts/test-parallel.sh --profile dev-managed --suite uci-dev || rc=$?
  bash scripts/test-parallel.sh --profile dev-managed --suite browser-dev || rc=$?
  return "$rc"
}

run_install() {
  bash scripts/pipeline.sh install
}

run_database_maintenance() {
  bash scripts/maintain-installed-database.sh "$@"
}

run_db_tests() {
  bash scripts/test-parallel.sh --profile db --suite db-health
  rm -rf build/extension/*/tests/regress_output
  bash scripts/test-parallel.sh --profile db --suite native-db
  bash scripts/test-parallel.sh --profile db --suite managed-db
}

run_publish() {
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
  local base="${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}"
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  python3 scripts/verify-application-release.py --base "$base" --timeout-seconds 60
}

# Product lifecycle owns build/install/database verification/publication/live checks.
# Corpus ingestion and foundation/model seeding are owned exclusively by seed.yml.
run_deploy() {
  check_deps
  run_build

  # Preserve downstream product evidence even if a development suite fails.
  # Build/install/runtime failures themselves still stop immediately under set -e.
  local dev_test_rc=0
  run_dev_tests || dev_test_rc=$?

  run_install
  run_database_maintenance --prepare
  run_db_tests
  run_publish
  reconcile_installed_product
  run_live_tests

  if (( dev_test_rc != 0 )); then
    echo "::error::development tests failed earlier (status $dev_test_rc); integrated lifecycle continued and retained downstream evidence" >&2
    return "$dev_test_rc"
  fi
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
  deploy|mainline)
    run_deploy
    ;;
esac
