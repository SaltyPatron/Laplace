#!/usr/bin/env bash
# Same-repository pull-request proof. The live install, services, canonical
# database, migrations and publication remain untouched. The exact branch-native
# PostgreSQL extensions execute before the broader DEV/BAT matrix so a broken
# database/product build fails fast instead of burning minutes on unrelated tests.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage=all
selected_phase=
list_phases=0
while (( $# )); do
  case "$1" in
    --stage) stage="$2"; shift 2 ;;
    --phase) selected_phase="$2"; shift 2 ;;
    --list-phases) list_phases=1; shift ;;
    *) echo "unknown PR proof option: $1" >&2; exit 2 ;;
  esac
done
[[ "$stage" == all || "$stage" == check ]] || { echo "unknown PR proof stage: $stage" >&2; exit 2; }

pr_phases() {
  echo policy
  [[ "$stage" != check ]] || return 0
  printf '%s\n' build private-db-start native-db operational-db highway-recovery legacy-repair-db private-db-stop \
    native-dev managed-dev uci-dev browser-dev proof-complete
}

run_suite() {
  bash scripts/test-parallel.sh --profile "$1" --suite "$2"
}

run_phase() {
  case "$1" in
    policy)
      python3 scripts/check-sql-manifest-dependencies.py
      bash scripts/test-parallel.sh --policy
      if [[ "$stage" == all ]]; then
        bash scripts/test-managed-publish-snapshot.sh
        bash scripts/test-runtime-reuse-idempotency.sh
      fi ;;
    build) bash scripts/pipeline.sh build ;;
    private-db-start|native-db|operational-db|highway-recovery|legacy-repair-db|private-db-stop)
      bash scripts/pr-db-proof.sh --phase "$1" ;;
    native-dev) run_suite dev-native native-dev ;;
    managed-dev|uci-dev|browser-dev) run_suite dev-managed "$1" ;;
    proof-complete)
      expected="|"
      while IFS= read -r completed; do
        [[ "$completed" == proof-complete ]] || expected+="$completed|"
      done < <(pr_phases)
      [[ "${LAPLACE_CI_COMPLETED_PHASES:-}" == "$expected" ]] || {
        echo "PR proof completion requires every preceding canonical phase" >&2
        return 2
      }
      echo "PR_PROOF_OK policy=green publish_snapshot=green runtime_reuse=green build=green native_db=green dev_bat=green production_mutations=0" ;;
    *) echo "unknown PR proof phase: $1" >&2; return 2 ;;
  esac
}

if [[ "$list_phases" == 1 ]]; then pr_phases; exit 0; fi

# shellcheck source=scripts/lib/storage.sh
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init

# Use a run-scoped database name at CMake configure time so concurrent/retried PR
# proofs cannot share a regression database. Local invocation gets a process-
# scoped fallback and is still forbidden from naming the canonical database.
run_id="${GITHUB_RUN_ID:-$$}"
run_attempt="${GITHUB_RUN_ATTEMPT:-1}"
export LAPLACE_REGRESS_DB="${LAPLACE_REGRESS_DB:-laplace_pr_${run_id}_${run_attempt}}"

if [[ -n "$selected_phase" ]]; then
  valid=0
  while IFS= read -r phase; do [[ "$phase" != "$selected_phase" ]] || valid=1; done < <(pr_phases)
  [[ "$valid" == 1 ]] || { echo "phase $selected_phase is not selected by stage $stage" >&2; exit 2; }
  run_phase "$selected_phase"
else
  trap 'bash scripts/pr-db-proof.sh --phase cleanup' EXIT
  export LAPLACE_CI_COMPLETED_PHASES="|"
  while IFS= read -r phase; do
    run_phase "$phase"
    LAPLACE_CI_COMPLETED_PHASES+="$phase|"
  done < <(pr_phases)
fi
