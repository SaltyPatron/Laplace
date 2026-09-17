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

# Repository-policy checks are an explicit `check` operation. They are not a
# prerequisite hidden inside build, deploy, database, or ingest operations.
run_ci_contract_checks() {
  bash -n \
    scripts/product-ci.sh \
    scripts/pipeline.sh \
    scripts/ci-deps.sh \
    scripts/test-parallel.sh \
    scripts/model-synthesize-ci.sh \
    scripts/maintain-installed-database.sh \
    scripts/ingest-source.sh \
    scripts/check-deployed-revision.sh
  python3 scripts/validate-pipeline.py
  python3 scripts/test-ci-workspace.py
  python3 scripts/test-product-ci-artifact-ownership.py
  python3 scripts/test-seed-workflow-ownership.py
}

require_built_revision() {
  local expected actual
  expected="$(git rev-parse HEAD)"
  actual="$(cat build/.laplace-source-revision 2>/dev/null || true)"
  if [[ "$actual" != "$expected" ]]; then
    echo "::error::prepared build does not belong to this checkout (expected $expected, found ${actual:-missing}); run product-ci.sh build or deploy first" >&2
    return 1
  fi
}

require_deployed_revision() {
  bash scripts/check-deployed-revision.sh "$(git rev-parse HEAD)"
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
  require_built_revision
  local rc=0
  bash scripts/test-parallel.sh --profile dev-native --suite native-dev || rc=$?
  bash scripts/test-parallel.sh --profile dev-managed --suite managed-dev || rc=$?
  bash scripts/test-parallel.sh --profile dev-managed --suite uci-dev || rc=$?
  bash scripts/test-parallel.sh --profile dev-managed --suite browser-dev || rc=$?
  return "$rc"
}

run_install() {
  require_built_revision
  bash scripts/pipeline.sh install
}

run_database_maintenance() {
  bash scripts/maintain-installed-database.sh "$@"
}

run_db_tests() {
  require_built_revision
  bash scripts/test-parallel.sh --profile db --suite db-health
  rm -rf build/extension/*/tests/regress_output
  bash scripts/test-parallel.sh --profile db --suite native-db
  bash scripts/test-parallel.sh --profile db --suite managed-db
}

run_publish() {
  bash scripts/publish-applications.sh recover
  require_built_revision
  bash scripts/publish-applications.sh deploy
}

run_live_tests() {
  require_built_revision
  require_deployed_revision
  export LAPLACE_API_BASE="${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}"
  bash scripts/test-parallel.sh --profile live --suite live-floor
  bash scripts/test-parallel.sh --profile live --suite live-api
  bash scripts/test-parallel.sh --profile live --suite managed-live
  bash scripts/test-parallel.sh --profile live --suite generation-eval
}

check_application_live() {
  local base="${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}"
  local body
  body="$(curl -fsS "$base/health")" || {
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
  local base="${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}"
  # Installed reconciliation may mutate canonical database state, so first prove
  # that the application selected on this host is the checkout being reconciled.
  require_deployed_revision
  # Installed product reconciliation is deliberately seed-independent. Corpus
  # admission remains a separate seed workflow and cannot be required to deploy code.
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  check_application_live
  check_t0_perfcache_runtime
  python3 scripts/verify-application-release.py --base "$base" --timeout-seconds 60
}

run_mainline() {
  check_deps
  run_build
  run_dev_tests
}

run_deploy() {
  # Deploy is the product path. Static policy/lint suites are available through
  # the explicit `check` stage and never block a requested product operation.
  check_deps
  run_build
  run_dev_tests
  run_install
  run_database_maintenance --prepare
  run_db_tests
  run_publish
  reconcile_installed_product
  run_live_tests
}

case "$stage" in
  provision)
    provision_deps
    ;;
  check)
    run_ci_contract_checks
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
  mainline)
    run_mainline
    ;;
  deploy)
    run_deploy
    ;;
esac
