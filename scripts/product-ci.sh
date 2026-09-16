#!/usr/bin/env bash
# One authoritative product lifecycle for CI and operator-dispatched delivery.
# pipeline.sh owns build/install/database primitives; this file owns their product order.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-all}"
# A push to main is a development event, not an operator request to install,
# mutate the shared database, ingest corpora, publish services, or benchmark.
# Keep the explicit `all` dispatch semantics for operators, but cap an ordinary
# push at the development-test boundary even if the workflow's historical
# default still passes `all`.
if [[ "${GITHUB_EVENT_NAME:-}" == "push" && "$stage" == "all" ]]; then
  stage="test"
fi
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

run_suite() {
  bash scripts/test-parallel.sh --profile "$1" --suite "$2"
}

run_install() (
  resume_chess_observation_if_needed
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  bash deploy/linux/managed-publish.sh preflight
  bash scripts/pipeline.sh install
  bash deploy/linux/managed-publish.sh preflight
)

run_database_maintenance() (
  resume_chess_observation_if_needed
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

ensure_required_lexical_foundation() {
  bash scripts/ensure-foundation.sh --required-lexical
}

seed_operational_memory() {
  # The versioned operational source ships with this executable generation.
  # Its per-file content completion skips unchanged artifacts; do not use
  # --force/ReObservePresent and turn a deployment into another witness.
  local proof_root="${LAPLACE_OPERATIONAL_PROOF_DIRECTORY:-/build/laplace/recovery/operational-product}"
  mkdir -p "$proof_root"
  operational_proof_directory="$(mktemp -d "$proof_root/invocation-XXXXXXXX")"
  if [[ -n "${LAPLACE_CI_SESSION_DIRECTORY:-}" ]]; then
    printf '%s\n' "$operational_proof_directory" > "$LAPLACE_CI_SESSION_DIRECTORY/operational-proof-directory"
  fi
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  LAPLACE_INGEST_MAX_UNITS=0 LAPLACE_INGEST_FORCE=0 \
    python3 scripts/verify-operational-seed.py --ingest --report "$operational_proof_directory/seed.json"
}

verify_operational_execution() {
  local seed_run_id remaining deadline=$((SECONDS + 900))
  if [[ -n "${LAPLACE_CI_SESSION_DIRECTORY:-}" ]]; then
    IFS= read -r operational_proof_directory < "$LAPLACE_CI_SESSION_DIRECTORY/operational-proof-directory"
    [[ -d "$operational_proof_directory" ]] || { echo "missing operational proof directory" >&2; return 1; }
  fi
  # Consume only this invocation's verified seed receipt. Never select a latest
  # source run or reuse a receipt from another publication attempt.
  seed_run_id="$(python3 - "$operational_proof_directory/seed.json" <<'PY'
import json
import sys
import uuid
from pathlib import Path
path = Path(sys.argv[1])
if path.stat().st_size > 1048576:
    raise SystemExit("operational seed receipt exceeds the 1 MiB metadata envelope")
report = json.loads(path.read_text(encoding="utf-8"))
if report.get("disposition") != "verified":
    raise SystemExit("operational seed receipt is not a verified invocation")
run_id = report["run"]["run_id"]
if not isinstance(run_id, str) or str(uuid.UUID(run_id)) != run_id:
    raise SystemExit("operational seed receipt has an invalid run identity")
print(run_id)
PY
)"
  remaining=$((deadline - SECONDS))
  (( remaining > 0 )) || return 124
  timeout --signal=TERM --kill-after=5s "${remaining}s" python3 scripts/verify-operational-task.py \
    --shape-file seeds/operational/tasks/en_define.json --seed-run-id "$seed_run_id" \
    --receipt "$operational_proof_directory/task.json"
  remaining=$((deadline - SECONDS))
  (( remaining > 0 )) || return 124
  timeout --signal=TERM --kill-after=5s "${remaining}s" python3 scripts/verify-operational-task.py \
    --proof-mode direct-relation --prompt 'The opposite of hot is' --operand hot \
    --shape-file seeds/operational/tasks/en_antonym.json \
    --exemplar-file seeds/operational/exemplars/en_antonym.conllu --seed-run-id "$seed_run_id" \
    --receipt "$operational_proof_directory/antonym-task.json"

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
  # A prior source transition owns the still-published producer generation and
  # must finish before publication can acquire or recover its own transaction.
  resume_chess_observation_if_needed
  trap recover_publish EXIT
  if [[ "$stage" == applications ]]; then
    bash scripts/publish-applications.sh deploy
    recover_publish
  else
    run_publish
  fi
  trap - EXIT
  # Published services now use the corrected observation recipe. Drain them through
  # the existing maintenance owner while retaining, rebuilding and reading back the
  # old calculated source; restarted services cannot reintroduce the previous recipe.
  LAPLACE_REPAIR_PUBLISHED_SOURCE="$(git rev-parse HEAD)" \
    python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" \
    --timeout-seconds "${LAPLACE_CHESS_OBSERVATION_TIMEOUT_SECONDS:-3600}" -- \
    bash scripts/repair-chess-position-outcomes.sh
}

run_live_suite() {
  if [[ "${LAPLACE_FRESH_DB:-}" == 1 && "${LAPLACE_RESTORE_FOUNDATION:-}" != 1 ]]; then
    echo "broader foundation restoration not requested after reset — full-foundation live suite skipped"
    return 0
  fi
  export LAPLACE_API_BASE="${LAPLACE_API_BASE:-http://127.0.0.1:8080}"
  run_suite live "$1"
}

# Delivery admits the lexical sources required by operational task execution.
# Broader foundation restoration and domain benchmarks retain their explicit owners.
product_phases() {
  echo policy
  if [[ "$stage" == reconcile ]]; then echo reconcile; return; fi
  [[ "$stage" != check ]] || return 0
  printf '%s\n' dependencies build
  [[ "$stage" != build ]] || return 0
  printf '%s\n' native-dev managed-dev uci-dev browser-dev
  [[ "$stage" != test ]] || return 0
  if [[ "$stage" == application-check || "$stage" == applications ]]; then
    echo application-check
    if [[ "$stage" == applications ]]; then
      printf '%s\n' lexical-foundation operational-seed publish operational-execution
    fi
    return 0
  fi
  printf '%s\n' native-install database-maintenance
  [[ "${LAPLACE_RESTORE_FOUNDATION:-}" != 1 ]] || echo foundation
  printf '%s\n' lexical-foundation operational-seed
  [[ "$stage" != deploy ]] || return 0
  if [[ "$stage" == all ]]; then
    echo publish
    echo operational-execution
  fi
  printf '%s\n' db-health native-db managed-db
  [[ "$stage" != integrate ]] || return 0
  if [[ "${LAPLACE_FRESH_DB:-}" != 1 || "${LAPLACE_RESTORE_FOUNDATION:-}" == 1 ]]; then
    printf '%s\n' live-floor live-api managed-live generation-eval
  fi
  [[ "${LAPLACE_GENERATION_BENCHMARK:-}" != 1 ]] || echo performance
}

run_phase() {
  if [[ -n "${LAPLACE_CI_SESSION_DIRECTORY:-}" && -f "$LAPLACE_CI_SESSION_DIRECTORY/operational-proof-directory" ]]; then
    IFS= read -r operational_proof_directory < "$LAPLACE_CI_SESSION_DIRECTORY/operational-proof-directory"
  fi
  case "$1" in
    policy) run_policy ;;
    reconcile) reconcile_installed_product ;;
    dependencies) run_deps ;;
    build) run_build ;;
    native-dev) run_suite dev-native native-dev ;;
    managed-dev|uci-dev|browser-dev) run_suite dev-managed "$1" ;;
    application-check)
      resume_chess_observation_if_needed
      [[ "${LAPLACE_FRESH_DB:-}" != 1 && "${LAPLACE_FULL_CLEAN:-}" != 1 ]] || {
        echo "application-only release cannot reset the database or discard install receipts" >&2
        return 1
      }
      bash scripts/publish-applications.sh check ;;
    native-install) run_install ;;
    database-maintenance) run_database_maintenance ;;
    foundation) restore_foundation_if_requested ;;
    lexical-foundation) ensure_required_lexical_foundation ;;
    operational-seed) seed_operational_memory ;;
    publish) run_publish_with_recovery ;;
    operational-execution) verify_operational_execution ;;
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
