#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-all}"
case "$stage" in
  reconcile|check|build|test|deploy|integrate|all|application-check|applications) ;;
  *) echo "unknown product stage: $stage" >&2; exit 2 ;;
esac

if [[ "$stage" == check ]]; then
  bash -n scripts/product-ci.sh scripts/pipeline.sh scripts/ci-deps.sh scripts/test-parallel.sh
  exit 0
fi

run_deps() {
  if [[ "${GITHUB_EVENT_NAME:-}" == push ]]; then
    bash scripts/ci-deps.sh --check-only
  else
    bash scripts/ci-deps.sh
  fi
}

run_build() {
  local args=()
  [[ "${LAPLACE_FULL_CLEAN:-}" != 1 ]] || args+=(--force-rebuild)
  [[ "${LAPLACE_FORCE_CODEGEN:-}" != 1 ]] || args+=(--force-codegen)
  bash scripts/pipeline.sh "${args[@]}" build
}

run_suite() {
  bash scripts/test-parallel.sh --profile "$1" --suite "$2"
}

run_install() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}" 60
  bash scripts/pipeline.sh install
}

run_database_maintenance() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}" 60
  bash scripts/maintain-installed-database.sh
}

reconcile_installed_product() {
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  curl -fsS http://127.0.0.1:5187/health/ready | grep -q '"ready":true'
}

run_publish() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}" 60
  bash scripts/publish-applications.sh deploy
}

run_live_suite() {
  export LAPLACE_API_BASE="${LAPLACE_API_BASE:-http://127.0.0.1:8080}"
  run_suite live "$1"
}

product_phases() {
  case "$stage" in
    reconcile)
      echo reconcile
      ;;
    build)
      printf '%s\n' dependencies build
      ;;
    test)
      printf '%s\n' dependencies build native-dev managed-dev uci-dev browser-dev
      ;;
    deploy)
      printf '%s\n' dependencies build native-install database-maintenance
      ;;
    integrate)
      printf '%s\n' dependencies build db-health native-db managed-db
      ;;
    application-check)
      printf '%s\n' dependencies build application-check
      ;;
    applications)
      printf '%s\n' dependencies build application-check publish
      ;;
    all)
      printf '%s\n' \
        dependencies build \
        native-dev managed-dev uci-dev browser-dev \
        native-install database-maintenance publish \
        db-health native-db managed-db \
        live-floor live-api managed-live generation-eval
      [[ "${LAPLACE_GENERATION_BENCHMARK:-}" != 1 ]] || echo performance
      ;;
  esac
}

run_phase() {
  case "$1" in
    reconcile) reconcile_installed_product ;;
    dependencies) run_deps ;;
    build) run_build ;;
    native-dev) run_suite dev-native native-dev ;;
    managed-dev|uci-dev|browser-dev) run_suite dev-managed "$1" ;;
    application-check) bash scripts/publish-applications.sh check ;;
    native-install) run_install ;;
    database-maintenance) run_database_maintenance ;;
    publish) run_publish ;;
    db-health|managed-db) run_suite db "$1" ;;
    native-db)
      rm -rf build/extension/*/tests/regress_output
      run_suite db native-db ;;
    live-floor|live-api|managed-live|generation-eval) run_live_suite "$1" ;;
    performance) bash scripts/test-parallel.sh --perf ;;
    *) echo "unknown product phase: $1" >&2; return 2 ;;
  esac
}

case "${2:-}" in
  --list-phases) product_phases ;;
  --phase)
    [[ $# == 3 ]] || { echo "--phase requires one phase name" >&2; exit 2; }
    selected_phase="$3"
    valid=0
    while IFS= read -r phase; do [[ "$phase" != "$selected_phase" ]] || valid=1; done < <(product_phases)
    [[ "$valid" == 1 ]] || { echo "phase $selected_phase is not selected by stage $stage" >&2; exit 2; }
    run_phase "$selected_phase" ;;
  '')
    while IFS= read -r phase; do run_phase "$phase"; done < <(product_phases) ;;
  *) echo "unknown product option: $2" >&2; exit 2 ;;
esac
