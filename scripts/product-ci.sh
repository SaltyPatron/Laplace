#!/usr/bin/env bash
# Product build/test/activation orchestration.
# Data ingestion and destructive database recreation have independent operator owners.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-all}"
# A push to main is development validation, not deployment or host mutation.
if [[ "${GITHUB_EVENT_NAME:-}" == "push" && "$stage" == "all" ]]; then
  stage="test"
fi
case "$stage" in
  reconcile|check|build|test|deploy|integrate|all|application-check|applications) ;;
  *) echo "unknown product stage: $stage" >&2; exit 2 ;;
esac

# Documentation/tooling/workflow-only pushes get cheap syntax validation only.
if [[ "${GITHUB_EVENT_NAME:-}" == "push" && "$stage" == "check" ]]; then
  bash -n scripts/product-ci.sh scripts/pipeline.sh scripts/ci-policy.sh scripts/ci-deps.sh
  python3 - <<'PY'
from pathlib import Path
import yaml
for path in sorted(Path('.github/workflows').glob('*.yml')):
    yaml.load(path.read_text(encoding='utf-8'), Loader=yaml.BaseLoader)
print('source-only syntax check passed')
PY
  exit 0
fi

run_policy() {
  bash scripts/ci-policy.sh
}

run_deps() {
  # Normal commits verify dependency state; only an explicit operator lifecycle
  # may provision/upgrade the persistent host dependency installation.
  if [[ "${GITHUB_EVENT_NAME:-}" == "push" ]]; then
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

run_install() (
  resume_chess_observation_if_needed
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  bash scripts/pipeline.sh install
)

run_database_maintenance() (
  resume_chess_observation_if_needed
  python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" -- \
    bash scripts/maintain-installed-database.sh
)

reconcile_installed_product() {
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  python3 scripts/verify-application-release.py --timeout-seconds 120
}

run_publish() {
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  bash scripts/publish-applications.sh deploy
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

resume_chess_observation_if_needed() {
  python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" \
    --resume-if-needed --timeout-seconds "${LAPLACE_CHESS_OBSERVATION_TIMEOUT_SECONDS:-3600}" -- \
    bash scripts/repair-chess-position-outcomes.sh
}

run_publish_with_recovery() {
  resume_chess_observation_if_needed
  trap recover_publish EXIT
  if [[ "$stage" == applications ]]; then
    bash scripts/publish-applications.sh deploy
    recover_publish
  else
    run_publish
  fi
  trap - EXIT
  LAPLACE_REPAIR_PUBLISHED_SOURCE="$(git rev-parse HEAD)" \
    python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" \
    --timeout-seconds "${LAPLACE_CHESS_OBSERVATION_TIMEOUT_SECONDS:-3600}" -- \
    bash scripts/repair-chess-position-outcomes.sh
}

run_live_suite() {
  export LAPLACE_API_BASE="${LAPLACE_API_BASE:-http://127.0.0.1:8080}"
  run_suite live "$1"
}

product_phases() {
  # Policy is pre-release/operator work. Normal pushes compile and test instead
  # of replaying the policy registry on every commit.
  [[ "${GITHUB_EVENT_NAME:-}" == "push" ]] || echo policy
  if [[ "$stage" == reconcile ]]; then echo reconcile; return; fi
  [[ "$stage" != check ]] || return 0

  printf '%s\n' dependencies build
  [[ "$stage" != build ]] || return 0

  printf '%s\n' native-dev managed-dev uci-dev browser-dev
  [[ "$stage" != test ]] || return 0

  if [[ "$stage" == application-check || "$stage" == applications ]]; then
    echo application-check
    [[ "$stage" != applications ]] || echo publish
    return 0
  fi

  # Product activation owns installation and non-destructive migration only.
  # Destructive recreation is db-ops; all ingestion is seed-*.
  printf '%s\n' native-install database-maintenance
  [[ "$stage" != deploy ]] || return 0

  [[ "$stage" != all ]] || echo publish
  printf '%s\n' db-health native-db managed-db
  [[ "$stage" != integrate ]] || return 0

  printf '%s\n' live-floor live-api managed-live generation-eval
  [[ "${LAPLACE_GENERATION_BENCHMARK:-}" != 1 ]] || echo performance
}

run_phase() {
  case "$1" in
    policy) run_policy ;;
    reconcile) reconcile_installed_product ;;
    dependencies) run_deps ;;
    build) run_build ;;
    native-dev) run_suite dev-native native-dev ;;
    managed-dev|uci-dev|browser-dev) run_suite dev-managed "$1" ;;
    application-check)
      [[ "${LAPLACE_FULL_CLEAN:-}" != 1 ]] || {
        echo "application-only release cannot discard install receipts" >&2
        return 1
      }
      bash scripts/publish-applications.sh check ;;
    native-install) run_install ;;
    database-maintenance) run_database_maintenance ;;
    publish) run_publish_with_recovery ;;
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
