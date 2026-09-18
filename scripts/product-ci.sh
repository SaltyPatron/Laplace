#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-build}"
case "$stage" in
  provision|reconcile|check|build|install|applications|deploy|proof|release-qualification|release-candidate|release-activation|proof-model|mainline|test-dev|test-db|test-live) ;;
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
  python3 scripts/test-workflow-architecture.py
  python3 scripts/test-benchmark-suite.py
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
  local native_rc=0
  bash scripts/test-parallel.sh --profile db --suite native-db || native_rc=$?
  if (( native_rc != 0 )); then
    while IFS= read -r diff; do
      echo "===== REGRESSION DIFF: $diff =====" >&2
      cat "$diff" >&2 || true
    done < <(find -L build -path '*/tests/regress_output/regression.diffs' -type f -print | sort)
    return "$native_rc"
  fi
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

run_release_qualification() {
  check_deps
  run_build
  run_dev_tests
}

run_mainline() {
  run_release_qualification
}

release_candidate_current_before_mutation() {
  [[ "${LAPLACE_SKIP_IF_SUPERSEDED:-0}" == 1 ]] || return 0

  local selected latest
  selected="$(git rev-parse HEAD)"
  latest="$(git ls-remote --heads origin refs/heads/main | awk '{print $1}')"
  if [[ -z "$latest" ]]; then
    echo "::error::release candidate could not resolve current main before mutation" >&2
    return 2
  fi
  if [[ "$latest" == "$selected" ]]; then
    return 0
  fi

  echo "::notice::release candidate $selected became superseded by $latest after build/dev qualification; install/database mutation skipped"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      echo "## Superseded release candidate"
      echo
      printf '%s\n' "- Selected: \`$selected\`"
      printf '%s\n' "- Current main: \`$latest\`"
      echo "- Qualification completed; install/database mutation was not started."
    } >> "$GITHUB_STEP_SUMMARY"
  fi
  return 3
}

run_release_candidate() {
  check_deps
  require_built_revision

  # This stage begins at the mutation boundary. Qualification is a separate,
  # preemptible job; once install starts, finish this candidate coherently.
  local current_rc=0
  release_candidate_current_before_mutation || current_rc=$?
  if (( current_rc == 3 )); then
    return 0
  fi
  (( current_rc == 0 )) || return "$current_rc"

  run_install
  run_database_maintenance --prepare
  run_db_tests
}

run_release_activation() {
  run_publish
  reconcile_installed_product
  run_live_tests
}

run_proof_model() {
  require_built_revision
  LAPLACE_MODEL_PROOF_CODE_CORPORA=1 bash scripts/model-synthesize-ci.sh
}

run_deploy() {
  # Local convenience composition. GitHub Actions owns these as three separate
  # jobs: read-only qualification, candidate mutation, then activation.
  run_release_qualification
  run_release_candidate
  run_release_activation
}

run_proof() {
  # Local convenience composition; CI uses the four modular stages directly.
  run_release_qualification
  run_release_candidate
  run_proof_model
  run_release_activation
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
  release-qualification)
    run_release_qualification
    ;;
  release-candidate)
    run_release_candidate
    ;;
  release-activation)
    run_release_activation
    ;;
  proof-model)
    run_proof_model
    ;;
  deploy)
    run_deploy
    ;;
  proof)
    run_proof
    ;;
esac
