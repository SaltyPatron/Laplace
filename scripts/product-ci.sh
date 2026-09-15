#!/usr/bin/env bash
# One authoritative product lifecycle for CI and operator-dispatched delivery.
# pipeline.sh owns build/install/database primitives; this file owns their product order.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Completion state belongs to this shell invocation, never its inherited environment.
stockfish_corpus_passed=0
operational_postchecks_passed=0
operational_seed_run_id=
stage="${1:-all}"
case "$stage" in
  reconcile|check|build|test|deploy|integrate|all|application-check|applications) ;;
  *) echo "unknown product stage: $stage" >&2; exit 2 ;;
esac

run_policy() {
  bash scripts/ci-policy.sh
}

resume_held_repair_if_needed() {
  # Resolve this product's exact held repair before installation/publication can
  # replace its native or managed generation. With no owned hold this is a no-op.
  python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" --resume-if-needed \
    --max-bytes 4294967296 --max-line-bytes 2097152 --max-prior-bytes 34359738368 \
    --max-current-readback-bytes 8589934592 --timeout-seconds 1800 -- \
    bash scripts/repair-legacy-content-lifecycle.sh "${PGDATABASE:-laplace}"
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
  local proof_root="${LAPLACE_OPERATIONAL_PROOF_DIRECTORY:-/build/laplace/recovery/operational-product}"
  mkdir -p "$proof_root"
  operational_proof_directory="$(mktemp -d "$proof_root/invocation-XXXXXXXX")"
  if [[ "${LAPLACE_FRESH_DB:-}" == 1 && "${LAPLACE_RESTORE_FOUNDATION:-}" != 1 ]]; then
    printf '%s\n' '{"disposition":"intentionally-unseeded","reason":"fresh database without foundation restoration"}' \
      > "$operational_proof_directory/disposition.json"
    return 0
  fi
  bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  LAPLACE_INGEST_MAX_UNITS=0 LAPLACE_INGEST_FORCE=0 \
    python3 scripts/verify-operational-seed.py --ingest --report "$operational_proof_directory/seed.json"
}

verify_operational_execution() {
  local receipt_prefix="${1:-}" seed_run_id remaining deadline=$((SECONDS + 900))
  case "$receipt_prefix" in ''|post-stockfish-) ;; *) return 2 ;; esac
  if [[ "${LAPLACE_FRESH_DB:-}" == 1 && "${LAPLACE_RESTORE_FOUNDATION:-}" != 1 ]]; then
    echo "fresh DB intentionally left unseeded — operational execution proof skipped"
    return 0
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
  if [[ "$receipt_prefix" == post-stockfish- ]]; then
    [[ "$seed_run_id" == "${operational_seed_run_id:?initial operational proof did not select its seed}" ]] || {
      echo "post-Stockfish seed differs from this process's initial verified seed" >&2
      return 1
    }
  else
    operational_seed_run_id="$seed_run_id"
  fi
  remaining=$((deadline - SECONDS))
  (( remaining > 0 )) || return 124
  timeout --signal=TERM --kill-after=5s "${remaining}s" python3 scripts/verify-operational-task.py \
    --shape-file seeds/operational/tasks/en_define.json --seed-run-id "$seed_run_id" \
    --receipt "$operational_proof_directory/${receipt_prefix}task.json"
  remaining=$((deadline - SECONDS))
  (( remaining > 0 )) || return 124
  timeout --signal=TERM --kill-after=5s "${remaining}s" python3 scripts/verify-operational-task.py \
    --proof-mode direct-relation --prompt 'The opposite of hot is' --operand hot \
    --shape-file seeds/operational/tasks/en_antonym.json \
    --exemplar-file seeds/operational/exemplars/en_antonym.conllu --seed-run-id "$seed_run_id" \
    --receipt "$operational_proof_directory/${receipt_prefix}antonym-task.json"
  if [[ "$receipt_prefix" == post-stockfish- ]]; then operational_postchecks_passed=1; fi
}

run_stockfish_corpus_acceptance() {
  # The caller already owns the shared host lock through publication, repaired
  # service restoration and both ordinary proofs. The common CLI additionally
  # owns the canonical ingest lane for its two exact observations.
  local output="${LAPLACE_STOCKFISH_CORPUS_DIRECTORY:-$operational_proof_directory/stockfish-corpus}"
  export TMPDIR=/build/laplace/work TMP=/build/laplace/work TEMP=/build/laplace/work
  dotnet build app/Laplace.Cli/Laplace.Cli.csproj -c Release --nologo -v minimal
  bash scripts/sync-managed-native-artifacts.sh
  python3 scripts/ingest-stockfish-corpus.py \
    --prefix "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}" --output "$output"
  stockfish_corpus_passed=1
}

record_chess_completion() {
  # These variables only become true after this invocation's actual commands
  # finish. Neither a prior receipt nor a later test outcome can authorize them.
  [[ "${stockfish_corpus_passed:-0}" == 1 && "${operational_postchecks_passed:-0}" == 1 ]] || return 0
  local source_sha
  source_sha="$(git rev-parse HEAD)"
  [[ -z "${GITHUB_SHA:-}" || "$source_sha" == "$GITHUB_SHA" ]] || {
    echo "completed chess acceptance source differs from this workflow revision" >&2
    return 1
  }
  case "$stage" in all|applications) ;; *) return 1 ;; esac
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    [[ "${GITHUB_RUN_ID:-}" =~ ^[0-9]+$ && "${GITHUB_RUN_ATTEMPT:-}" =~ ^[1-9][0-9]*$ ]] || return 1
    printf 'chess_benchmark_ready=true\nactivated_ref=%s\nchess_acceptance_stage=%s\n' \
      "$source_sha" "$stage" >> "$GITHUB_OUTPUT"
  fi
}

run_recorded_chess_benchmark() {
  # Use the activated application's ordinary recording path while this lifecycle
  # still owns the host lock. Retain the runner's failure receipt before exit.
  python3 scripts/benchmark-recorded-chess.py \
    --output-dir "${LAPLACE_RECORDED_CHESS_DIRECTORY:-$operational_proof_directory/recorded-chess}"
}

observe_chess_runtime() {
  # Observe the already-published services before corpus work can fail. This
  # starts one owned Stockfish probe, preserving the actual service lifetimes.
  python3 scripts/benchmark-chess-environment.py --runtime-only \
    --output-dir "${LAPLACE_CHESS_RUNTIME_DIRECTORY:-$operational_proof_directory/chess-runtime}" \
    --reserve-cpus 0 --cpu-budget 1 --memory-mb 512 --max-seconds 60
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
    python3 scripts/quiesce-managed-database.py --database "${PGDATABASE:-laplace}" \
      --max-bytes 4294967296 --max-line-bytes 2097152 --max-prior-bytes 34359738368 \
      --max-current-readback-bytes 8589934592 --timeout-seconds 1800 -- \
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
case "$stage" in
  reconcile|deploy|integrate|all|applications) resume_held_repair_if_needed ;;
esac
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
    observe_chess_runtime
    # This path retains its unchanged-native publication contract. It does not
    # claim installation, migration or corpus repair; it explicitly admits and
    # proves the operational source used by this application's corpus checks.
    seed_operational_memory
    verify_operational_execution
    run_stockfish_corpus_acceptance
    verify_operational_execution post-stockfish-
    record_chess_completion
    run_recorded_chess_benchmark
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
observe_chess_runtime
# Repair owns its restoration and unknown transaction outcomes. Publication's
# API recovery must not restart a writer after unresolved repair quiescence.
run_repair_installed_corpus
verify_operational_execution
run_stockfish_corpus_acceptance
verify_operational_execution post-stockfish-
record_chess_completion
run_recorded_chess_benchmark
run_integration
run_live_if_expected
run_perf
