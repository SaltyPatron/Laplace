#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-build}"
case "$stage" in
  provision|reconcile|check|build|install|database|foundation|applications|deploy|mainline|test-dev|test-db|test-live) ;;
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

run_foundation() {
  bash scripts/ensure-foundation.sh
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

check_application_live() {
  local body
  body="$(curl -fsS http://127.0.0.1:5187/health)" || {
    echo "::error::installed laplace-api is not reachable after publication" >&2
    return 1
  }
  if ! grep -q '"status":"ok"' <<<"$body"; then
    echo "::error::installed laplace-api returned an unexpected liveness document: $body" >&2
    return 1
  fi
}

check_t0_perfcache_runtime() {
  local host="${PGHOST:-/var/run/postgresql}"
  local user="${PGUSER:-laplace_admin}"
  local database="${PGDATABASE:-laplace}"
  local word_id
  word_id="$(psql -h "$host" -U "$user" -d "$database" -v ON_ERROR_STOP=1 -X -tAc \
    "SELECT encode(laplace.word_id('the'),'hex');")" || {
    echo "::error::installed PostgreSQL runtime cannot execute the T0 perfcache-backed word_id operation" >&2
    return 1
  }
  if [[ ! "$word_id" =~ ^[0-9A-Fa-f]{32}$ ]]; then
    echo "::error::installed T0 perfcache probe returned an invalid identity: $word_id" >&2
    return 1
  fi
}

reconcile_installed_product() {
  # This phase is intentionally seed-agnostic. Database/schema installation,
  # application liveness, and the T0 runtime must not depend on foundation ingest.
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  check_application_live
  check_t0_perfcache_runtime
}

verify_seeded_product() {
  local body
  body="$(curl -sS http://127.0.0.1:5187/health/ready)" || {
    echo "::error::laplace-api seeded readiness endpoint is unreachable after foundation" >&2
    return 1
  }
  if ! grep -q '"ready":true' <<<"$body"; then
    echo "::error::laplace-api is not product-ready after foundation: $body" >&2
    return 1
  fi
  echo "PRODUCT_READY $body"
}

run_deploy() {
  check_deps
  run_build

  # A development-suite failure must remain visible and keep the workflow red,
  # but it must not erase downstream install/database/application evidence. A
  # genuine build/install/runtime failure still stops immediately under set -e.
  local dev_test_rc=0
  run_dev_tests || dev_test_rc=$?

  run_install
  run_database_maintenance --prepare

  # Publication proves the installed application process, not knowledge volume.
  # Structural DB/T0 reconciliation is seed-agnostic; only after that succeeds do
  # we admit/resume foundation data and require the full product-readiness contract.
  run_publish
  reconcile_installed_product
  run_foundation
  verify_seeded_product

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
  database)
    run_database_maintenance
    ;;
  foundation)
    run_foundation
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