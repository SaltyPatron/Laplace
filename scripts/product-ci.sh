#!/usr/bin/env bash
# One authoritative product lifecycle for CI and operator-dispatched delivery.
# pipeline.sh owns build/install/database primitives; this file owns their product order.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-all}"
case "$stage" in
  reconcile|check|build|test|deploy|integrate|all|application-check|applications) ;;
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

  # Use the already installed fixed service controls. The command holds their
  # managed transaction through migration, discards writer processes,
  # and restores only the services that were running before maintenance.
  python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" -- \
    bash scripts/maintain-installed-database.sh
)

ensure_product_foundation() {
  if bash scripts/ensure-foundation.sh --check-only; then
    echo "product foundation already complete — no ingest"
    return 0
  fi
  echo "product foundation incomplete — explicit restore requested"
  bash scripts/ensure-foundation.sh
  bash scripts/check-substrate-floor.sh "${PGDATABASE:-laplace}"
}

restore_foundation_if_requested() {
  if [[ "${LAPLACE_RESTORE_FOUNDATION:-}" == 1 ]]; then
    ensure_product_foundation
  else
    echo "foundation restore not requested — no ingest"
  fi
}

seed_operational_memory() {
  # The versioned operational source ships with this executable generation.
  # Its per-file content completion skips unchanged artifacts; do not use
  # --force/ReObservePresent and turn a deployment into another witness.
  if [[ "${LAPLACE_FRESH_DB:-}" == 1 && "${LAPLACE_RESTORE_FOUNDATION:-}" != 1 ]]; then
    return 0
  fi
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  LAPLACE_INGEST_MAX_UNITS=0 LAPLACE_INGEST_FORCE=0 \
    python3 scripts/verify-operational-seed.py --ingest
}

reconcile_installed_product() {
  # Fast source/tooling path: reconcile installed derived state and prove
  # application health. Never build and never seed corpus content.
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  python3 scripts/verify-application-release.py --timeout-seconds 120
}

run_publish() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  bash scripts/publish-applications.sh deploy
}

run_repair_installed_corpus() {
  # Publication has activated this source generation. Reclassification must not
  # restart the previous managed producer after changing its cached identities.
  LAPLACE_REPAIR_PUBLISHED_SOURCE="$(git rev-parse HEAD)" \
    python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" -- \
      bash scripts/repair-legacy-content-lifecycle.sh "${PGDATABASE:-laplace}"
}

ensure_api_running() {
  sudo -n systemctl start laplace-api || true
  sleep 3
  curl -fsS http://127.0.0.1:5187/health | grep -q '"status":"ok"' || {
    echo "::warning::laplace-api unhealthy after application recovery" >&2
    journalctl -u laplace-api -n 40 --no-pager 2>/dev/null \
      || sudo -n systemctl status laplace-api || true
    return 1
  }
}

recover_publish() {
  local recovery_rc=0 health_rc=0
  bash scripts/publish-applications.sh recover || recovery_rc=$?
  ensure_api_running || health_rc=$?
  [[ "$recovery_rc" -eq 0 && "$health_rc" -eq 0 ]]
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

run_live_if_expected() {
  if [[ "${LAPLACE_FRESH_DB:-}" == 1 && "${LAPLACE_RESTORE_FOUNDATION:-}" != 1 ]]; then
    echo "fresh DB intentionally left unseeded — seeded live product proof skipped"
    return 0
  fi
  run_live
}

run_perf() {
  [[ "${LAPLACE_GENERATION_BENCHMARK:-}" == 1 ]] || return 0
  bash scripts/test-parallel.sh --perf
}

run_policy
if [[ "$stage" == reconcile ]]; then
  reconcile_installed_product
  exit 0
fi
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
restore_foundation_if_requested
seed_operational_memory
if [[ "$stage" == deploy ]]; then
  echo "native/database stage complete; application publication and corpus repair belong to the full lifecycle"
  exit 0
fi

if [[ "$stage" == integrate ]]; then
  echo "integration-only stage verifies the installed database; it does not publish applications or repair retained content"
  run_integration
  exit 0
fi

trap recover_publish EXIT
run_publish
trap - EXIT
# Repair owns its restoration and unknown transaction outcomes. Publication's
# API recovery must not restart a writer after unresolved repair quiescence.
run_repair_installed_corpus
run_integration
run_live_if_expected
run_perf
