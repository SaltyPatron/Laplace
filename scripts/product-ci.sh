#!/usr/bin/env bash
# One authoritative product lifecycle for CI and operator-dispatched delivery.
# pipeline.sh owns build/install/database primitives; this file owns their product order.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-all}"
case "$stage" in
  check|build|test|deploy|integrate|all|application-check|applications) ;;
  *) echo "unknown product stage: $stage" >&2; exit 2 ;;
esac

run_policy() {
  bash scripts/ci-policy.sh
}

run_deps() {
  bash scripts/ci-deps.sh
}

run_build() {
  local args=()
  [[ "${LAPLACE_FULL_CLEAN:-}" != 1 ]] || args+=(--force-rebuild)
  [[ "${LAPLACE_FORCE_CODEGEN:-}" != 1 ]] || args+=(--force-codegen)
  bash scripts/pipeline.sh "${args[@]}" build
}

run_dev() {
  local args=(--engine)
  [[ "${LAPLACE_TEST_SERIAL:-}" != 1 ]] || args=(--serial --engine)
  bash scripts/test-parallel.sh "${args[@]}"
}

run_install_and_db() (
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  bash deploy/linux/managed-publish.sh preflight
  bash scripts/pipeline.sh install
  bash deploy/linux/managed-publish.sh preflight

  local api_was_active=0
  if systemctl is-active --quiet laplace-api; then
    api_was_active=1
    sudo -n systemctl stop laplace-api
  fi
  restore_api_after_db() {
    [[ "$api_was_active" -eq 0 ]] || sudo -n systemctl start laplace-api || true
  }
  trap restore_api_after_db EXIT

  local args=()
  [[ "${LAPLACE_FRESH_DB:-}" != 1 ]] || args+=(--fresh-db)
  bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
)

run_publish() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  bash scripts/publish-applications.sh deploy
}

recover_publish() {
  bash scripts/publish-applications.sh recover || true
}

run_integration() {
  rm -rf build/extension/*/tests/regress_output
  local args=(--integration)
  [[ "${LAPLACE_TEST_SERIAL:-}" != 1 ]] || args=(--serial --integration)
  bash scripts/test-parallel.sh "${args[@]}"
}

run_live() {
  LAPLACE_API_BASE="${LAPLACE_API_BASE:-http://127.0.0.1:8080}" \
    bash scripts/test-parallel.sh --app-live
}

run_perf() {
  [[ "${LAPLACE_GENERATION_BENCHMARK:-}" == 1 ]] || return 0
  bash scripts/test-parallel.sh --perf
}

run_policy
[[ "$stage" == check ]] && exit 0

run_deps
run_build
[[ "$stage" == build ]] && exit 0

run_dev
[[ "$stage" == test ]] && exit 0

if [[ "$stage" == application-check || "$stage" == applications ]]; then
  [[ "${LAPLACE_FRESH_DB:-}" != 1 && "${LAPLACE_FULL_CLEAN:-}" != 1 ]] || {
    echo "application-only release cannot reset the database or discard install receipts" >&2
    exit 1
  }
  bash scripts/publish-applications.sh check
  if [[ "$stage" == applications ]]; then
    trap recover_publish EXIT
    bash scripts/publish-applications.sh deploy
    recover_publish
    trap - EXIT
  fi
  exit 0
fi

run_install_and_db
[[ "$stage" == deploy ]] && exit 0

if [[ "$stage" == integrate ]]; then
  run_integration
  exit 0
fi

trap recover_publish EXIT
run_publish
run_integration
run_live
run_perf
recover_publish
trap - EXIT
