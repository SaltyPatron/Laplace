#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-build}"
case "$stage" in
  provision|reconcile|check|build|install|applications|deploy|proof|release-qualification|release-delivery|release-candidate|release-activation|proof-model|mainline|test-dev|test-db|test-live) ;;
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
  local ci_tmp="${RUNNER_TEMP:-$ROOT/build/ci-policy-tmp}"
  export LAPLACE_WORK_ROOT="$ci_tmp/laplace-work"
  export LAPLACE_CHESS_PGN_CACHE="$LAPLACE_WORK_ROOT/chess-pgn-validation"
  mkdir -p "$ci_tmp" "$LAPLACE_WORK_ROOT" "$LAPLACE_CHESS_PGN_CACHE"
  export TMPDIR="$ci_tmp" TMP="$ci_tmp" TEMP="$ci_tmp"

  bash -n \
    scripts/product-ci.sh \
    scripts/pipeline.sh \
    scripts/ci-deps.sh \
    scripts/test-parallel.sh \
    scripts/test-suites/*.sh \
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
  python3 scripts/test-ci-impact-plan.py
  python3 scripts/test-ci-qualification-cache.py
  python3 scripts/test-ci-product-freshness.py
  python3 scripts/test-ci-managed-projects.py
  python3 scripts/test-managed-policy.py
  python3 scripts/test-application-payload.py
  python3 scripts/test-cutechess-calibration.py
  python3 scripts/test-chess-x11-runtime.py
  python3 scripts/test-chess-floor-artifacts.py
  python3 scripts/test-recorded-chess-selection.py
  python3 scripts/test-chess-environment-benchmark.py ChessEnvironmentTests
  python3 scripts/test-managed-db-scheduling.py
  python3 scripts/test-codegen-configure.py
  python3 scripts/test-cmake-release.py
  python3 scripts/test-web-artifact.py
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

reuse_qualified_native_build() {
  [[ -f build/engine/core/liblaplace_core.so ]] && return 0

  local source_sha source_root work_root
  source_sha="$(python3 scripts/ci-qualification-cache.py source --suite native-dev 2>/dev/null || true)"
  [[ "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]] || return 1

  work_root="${LAPLACE_WORK_ROOT:-/build/laplace/work}"
  source_root="$work_root/product-worktrees/$source_sha"
  [[ -f "$source_root/build/engine/core/liblaplace_core.so" ]] || return 1
  find -L "$source_root/build/engine" -name 'laplace_t0_perfcache*.bin' -print -quit | grep -q . || return 1

  mkdir -p build
  [[ ! -e build/engine && ! -L build/engine ]] || return 1
  ln -s "$source_root/build/engine" build/engine
  echo "::notice::reusing qualified native build from $source_sha"
  return 0
}

run_build() {
  local args=() selected="${LAPLACE_BUILD_COMPONENTS:-all}"
  local need_native=0 need_managed=0 need_web=0

  [[ "${LAPLACE_FULL_CLEAN:-}" != 1 ]] || args+=(--force-rebuild)
  if [[ "${LAPLACE_FORCE_CODEGEN:-}" == 1 || \
        ! -f extension/laplace_substrate/sql/generated/seed_relation_types.sql.in || \
        ! -f extension/laplace_substrate/sql/generated/seed_pos.sql.in ]]; then
    args+=(--force-codegen)
  fi

  if [[ "$selected" == all || ",$selected," == *",native,"* ]]; then
    need_native=1
  fi
  if [[ "$selected" == all || ",$selected," == *",managed,"* ]]; then
    need_managed=1
  fi
  if [[ "$selected" == all || ",$selected," == *",web,"* ]]; then
    need_web=1
  fi

  # The checked-in OpenAPI document is an explicit web build input. API
  # contract changes select both managed and web in the impact plan; a pure web
  # change therefore does not need an unrelated managed rebuild.

  # Managed applications execute the native core from the candidate build tree.
  # When native inputs are unchanged, reference the immutable build belonging to
  # the matching native qualification receipt instead of rebuilding it.
  if (( need_native == 0 && need_managed == 1 )); then
    if ! reuse_qualified_native_build; then
      echo "::notice::no reusable qualified native build found; native build required"
      need_native=1
    fi
  fi

  local phases=()
  if (( need_native == 1 )); then
    [[ ! -L build/engine ]] || rm -f build/engine
    phases+=(build-native)
  fi
  (( need_managed == 0 )) || phases+=(build-app)
  (( need_web == 0 )) || phases+=(build-web)

  if (( ${#phases[@]} == 0 )); then
    echo "::notice::candidate requires no native/managed compilation"
  else
    bash scripts/pipeline.sh "${args[@]}" "${phases[@]}"
  fi

  mkdir -p build
  git rev-parse HEAD > build/.laplace-source-revision
}

run_dev_test_matrix() {
  local check_superseded="${1:-0}"
  local current_rc profile suite spec
  local selected="${LAPLACE_DEV_SUITES:-all}"
  local use_cache="${LAPLACE_USE_QUALIFICATION_CACHE:-0}"
  local specs=(
    "dev-native:native-dev"
    "dev-managed:managed-dev"
    "dev-managed:uci-dev"
    "dev-managed:browser-dev"
  )

  suite_selected() {
    local wanted="$1"
    [[ "$selected" == all ]] && return 0
    [[ -n "$selected" ]] || return 1
    [[ ",$selected," == *",$wanted,"* ]]
  }

  for spec in "${specs[@]}"; do
    if [[ "$check_superseded" == 1 ]]; then
      current_rc=0
      release_selected_revision_current || current_rc=$?
      if (( current_rc == 3 )); then return 3; fi
      (( current_rc == 0 )) || return "$current_rc"
    fi

    IFS=: read -r profile suite <<< "$spec"
    if ! suite_selected "$suite"; then
      echo "::notice::qualification planner kept $suite valid; suite not scheduled"
      continue
    fi

    if [[ "$use_cache" == 1 ]] && python3 scripts/ci-qualification-cache.py check --suite "$suite"; then
      echo "::notice::reusing passed qualification receipt for $suite"
      continue
    fi

    current_rc=0
    bash scripts/test-parallel.sh --profile "$profile" --suite "$suite" || current_rc=$?
    (( current_rc == 0 )) || return "$current_rc"

    if [[ "$use_cache" == 1 ]]; then
      python3 scripts/ci-qualification-cache.py record \
        --suite "$suite" --source-sha "$(git rev-parse HEAD)"
    fi
  done

  if [[ "$check_superseded" == 1 ]]; then
    current_rc=0
    release_selected_revision_current || current_rc=$?
    if (( current_rc == 3 )); then return 3; fi
    (( current_rc == 0 )) || return "$current_rc"
  fi
  return 0
}

run_dev_tests() {
  require_built_revision
  run_dev_test_matrix 0
}

run_install() {
  require_built_revision
  bash scripts/pipeline.sh install
}

run_database_maintenance() {
  bash scripts/maintain-installed-database.sh "$@"
}

csv_selected() {
  local selected="${1:-all}" wanted="$2"
  [[ "$selected" == all ]] || [[ ",$selected," == *",$wanted,"* ]]
}

force_full_carry_forward_impact() {
  export LAPLACE_BUILD_COMPONENTS=all
  export LAPLACE_MANAGED_BUILD_PROJECTS=all
  export LAPLACE_MANAGED_TEST_PROJECTS=all
  export LAPLACE_MANAGED_DB_TEST_PROJECTS=all
  export LAPLACE_MANAGED_LIVE_TEST_PROJECTS=all
  export LAPLACE_DEV_SUITES=all
  export LAPLACE_DB_SUITES=all
  export LAPLACE_LIVE_SUITES=all
  export LAPLACE_DELIVERY_ACTIONS=all
  export LAPLACE_PUBLISH_SCOPE=full
}

carry_forward_undelivered_impact() {
  # Only automatic main delivery owns this reconciliation. Manual/operator stages
  # deliberately keep the scope they were dispatched with.
  [[ "${LAPLACE_SKIP_IF_SUPERSEDED:-0}" == 1 ]] || return 0
  case "${LAPLACE_STAGE:-}" in
    release-qualification|release-delivery) ;;
    *) return 0 ;;
  esac

  local target deployed receipt plan
  target="$(git rev-parse HEAD)"
  receipt="${LAPLACE_APP_DIR:-/opt/laplace/app}/.laplace-source-revision"
  deployed="$(cat "$receipt" 2>/dev/null || true)"

  if [[ ! "$deployed" =~ ^[0-9a-fA-F]{40}$ ]]; then
    echo "::warning::installed application revision receipt is unavailable; carrying full product impact forward"
    force_full_carry_forward_impact
    return 0
  fi
  if [[ "$deployed" == "$target" ]]; then
    return 0
  fi

  if ! git cat-file -e "$deployed^{commit}" 2>/dev/null; then
    if ! git fetch --no-tags --depth=1 origin "$deployed"; then
      echo "::warning::could not resolve deployed revision $deployed; carrying full product impact forward"
      force_full_carry_forward_impact
      return 0
    fi
  fi

  if ! plan="$(python3 scripts/ci-impact-plan.py --root "$PWD" --base "$deployed" --head "$target")"; then
    echo "::warning::could not compute deployed-to-target impact; carrying full product impact forward"
    force_full_carry_forward_impact
    return 0
  fi

  eval "$(CARRY_PLAN_JSON="$plan" python3 - <<'PY'
import json
import os
import shlex

plan = json.loads(os.environ["CARRY_PLAN_JSON"])

orders = {
    "LAPLACE_BUILD_COMPONENTS": ("build_components", ("native", "managed", "web")),
    "LAPLACE_DEV_SUITES": ("dev_suites", ("native-dev", "managed-dev", "uci-dev", "browser-dev")),
    "LAPLACE_DB_SUITES": ("db_suites", ("db-health", "native-db", "managed-db")),
    "LAPLACE_LIVE_SUITES": ("live_suites", ("live-floor", "live-api", "managed-live", "generation-eval")),
    "LAPLACE_DELIVERY_ACTIONS": ("delivery_actions", ("install", "database", "reconcile", "publish", "live")),
}

for env_name, (field, order) in orders.items():
    current = os.environ.get(env_name, "")
    if current == "all":
        value = "all"
    else:
        selected = {item for item in current.split(",") if item}
        selected.update(plan.get(field, []))
        value = ",".join(item for item in order if item in selected)
    print(f"export {env_name}={shlex.quote(value)}")

project_fields = {
    "LAPLACE_MANAGED_BUILD_PROJECTS": "managed_build_projects",
    "LAPLACE_MANAGED_TEST_PROJECTS": "managed_test_projects",
    "LAPLACE_MANAGED_DB_TEST_PROJECTS": "managed_db_test_projects",
    "LAPLACE_MANAGED_LIVE_TEST_PROJECTS": "managed_live_test_projects",
}
for env_name, field in project_fields.items():
    current = os.environ.get(env_name, "")
    incoming = plan.get(field, [])
    if current == "all" or "all" in incoming:
        value = "all"
    else:
        selected = {item for item in current.split(",") if item}
        selected.update(incoming)
        value = ",".join(sorted(selected))
    print(f"export {env_name}={shlex.quote(value)}")

scope = os.environ.get("LAPLACE_PUBLISH_SCOPE", "api")
if scope == "full" or plan.get("publish_scope") == "full":
    scope = "full"
print(f"export LAPLACE_PUBLISH_SCOPE={shlex.quote(scope)}")
PY
)"

  echo "::notice::carried forward undelivered impact from $deployed to $target: build=$LAPLACE_BUILD_COMPONENTS managed_build=$LAPLACE_MANAGED_BUILD_PROJECTS managed_tests=$LAPLACE_MANAGED_TEST_PROJECTS dev=$LAPLACE_DEV_SUITES db=$LAPLACE_DB_SUITES delivery=$LAPLACE_DELIVERY_ACTIONS publish=$LAPLACE_PUBLISH_SCOPE live=$LAPLACE_LIVE_SUITES"
}

run_db_tests() {
  require_built_revision
  local selected="${LAPLACE_DB_SUITES:-all}"

  if csv_selected "$selected" db-health; then
    bash scripts/test-parallel.sh --profile db --suite db-health
  else
    echo "::notice::database planner kept db-health valid; suite not scheduled"
  fi

  if csv_selected "$selected" native-db; then
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
  else
    echo "::notice::database planner kept native-db valid; suite not scheduled"
  fi

  if csv_selected "$selected" managed-db; then
    bash scripts/test-parallel.sh --profile db --suite managed-db
  else
    echo "::notice::database planner kept managed-db valid; suite not scheduled"
  fi
}
run_publish() {
  local scope="${LAPLACE_PUBLISH_SCOPE:-full}"
  case "$scope" in
    api) bash scripts/publish-applications.sh api-recover ;;
    full|all) bash scripts/publish-applications.sh recover ;;
    *)
      echo "::error::unknown publication scope: $scope" >&2
      return 2
      ;;
  esac

  require_built_revision
  case "$scope" in
    api) bash scripts/publish-applications.sh api-deploy ;;
    full|all) bash scripts/publish-applications.sh deploy ;;
  esac
}
run_live_tests() {
  require_deployed_revision
  local selected="${LAPLACE_LIVE_SUITES:-all}"
  export LAPLACE_API_BASE="${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}"

  if csv_selected "$selected" live-floor; then
    bash scripts/test-parallel.sh --profile live --suite live-floor
  else
    echo "::notice::live planner kept live-floor valid; suite not scheduled"
  fi
  if csv_selected "$selected" live-api; then
    bash scripts/test-parallel.sh --profile live --suite live-api
  else
    echo "::notice::live planner kept live-api valid; suite not scheduled"
  fi
  if csv_selected "$selected" managed-live; then
    bash scripts/test-parallel.sh --profile live --suite managed-live
  else
    echo "::notice::live planner kept managed-live valid; suite not scheduled"
  fi
  if csv_selected "$selected" generation-eval; then
    bash scripts/test-parallel.sh --profile live --suite generation-eval
  else
    echo "::notice::live planner kept generation-eval valid; suite not scheduled"
  fi
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
  local word_id db_receipt perfcache_path file_receipt

  word_id="$(psql -h "$host" -U "$user" -d "$database" -v ON_ERROR_STOP=1 -X -tAc \
    "SELECT encode(laplace.word_id('the'),'hex');")" || {
    echo "::error::installed PostgreSQL runtime cannot execute the T0 perfcache-backed word_id operation" >&2
    return 1
  }
  if [[ ! "$word_id" =~ ^[0-9A-Fa-f]{32}$ ]]; then
    echo "::error::installed T0 perfcache probe returned an invalid identity: $word_id" >&2
    return 1
  fi

  # The T0 ROM is prewarmed by the postmaster. Replacing the file or recycling
  # backends is not enough: a live postmaster can keep serving the old mmap.
  # Compare the checksum receipt of the loaded native table with the trailer of
  # the exact file named by the runtime GUC. SQL is only exposing the native
  # receipt; it does not reconstruct any Tier-0 geometry.
  perfcache_path="$(psql -h "$host" -U "$user" -d "$database" -v ON_ERROR_STOP=1 -X -tAc \
    "SHOW laplace_substrate.perfcache_path;")" || {
    echo "::error::could not resolve installed T0 perfcache path" >&2
    return 1
  }
  db_receipt="$(psql -h "$host" -U "$user" -d "$database" -v ON_ERROR_STOP=1 -X -tAc \
    "SELECT encode(laplace.perfcache_receipt(),'hex');")" || {
    echo "::error::installed PostgreSQL runtime cannot expose its T0 perfcache receipt" >&2
    return 1
  }
  file_receipt="$(python3 - "$perfcache_path" <<'PY'
import pathlib
import sys
path = pathlib.Path(sys.argv[1])
data = path.read_bytes()
if len(data) < 16:
    raise SystemExit("T0 perfcache is shorter than its 16-byte receipt")
print(data[-16:].hex())
PY
)" || {
    echo "::error::could not read deployed T0 perfcache receipt from $perfcache_path" >&2
    return 1
  }

  if [[ "${db_receipt,,}" != "${file_receipt,,}" ]]; then
    echo "::error::T0 ROM mismatch: PostgreSQL mmap=$db_receipt deployed-file=$file_receipt. Restart the PostgreSQL postmaster; backend recycle alone cannot replace a shared-preload mmap." >&2
    return 1
  fi
  echo "::notice::T0 ROM receipt aligned: $db_receipt"

  local proof
  proof="$(curl -fsS -X POST "${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}/v1/explore/storage-proof" \
    -H 'Content-Type: application/json' \
    --data '{"text":"A"}')" || {
      echo "::error::deployed storage-proof endpoint is unavailable" >&2
      return 1
    }
  PROOF_JSON="$proof" DB_RECEIPT="$db_receipt" python3 - <<'PY' || {
import json
import math
import os

proof = json.loads(os.environ["PROOF_JSON"])
expected = os.environ["DB_RECEIPT"].lower()
assert proof["atom_window"] == 0x110000, proof
assert proof["perfcache_aligned"] is True, proof
assert proof["perfcache_receipt_hex"].lower() == expected, proof
assert proof["database_perfcache_receipt_hex"].lower() == expected, proof
nodes = proof["nodes"]
assert len(nodes) == 1, nodes
leaf = nodes[0]
assert leaf["tier"] == 0 and leaf["atom"] == 65, leaf
assert leaf["ducet_rank"] is not None, leaf
assert len(leaf["id_hex"]) == 32 and len(leaf["hilbert_hex"]) == 32, leaf
assert math.isclose(float(leaf["radius"]), 1.0, rel_tol=0.0, abs_tol=1e-12), leaf
PY
    echo "::error::deployed storage-proof endpoint does not agree with the live T0 ROM" >&2
    printf '%s\n' "$proof" >&2
    return 1
  }
  echo "::notice::storage-proof endpoint verified against live T0 ROM"
}

verify_installed_product() {
  local base="${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}"
  require_deployed_revision
  bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  check_application_live
  check_t0_perfcache_runtime
  python3 scripts/verify-application-release.py --base "$base" --timeout-seconds 60
}

reconcile_installed_product() {
  # Reconciliation is a deliberate database mutation and is only selected when
  # the impact plan invalidates installed substrate/database state.
  require_deployed_revision
  bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"
  verify_installed_product
}
run_release_qualification() {
  check_deps
  carry_forward_undelivered_impact

  # Do not spend build/test time on a revision that was already superseded
  # while waiting for the self-hosted runner.
  local current_rc=0
  release_selected_revision_current || current_rc=$?
  if (( current_rc == 3 )); then return 0; fi
  (( current_rc == 0 )) || return "$current_rc"

  run_build

  current_rc=0
  release_selected_revision_current || current_rc=$?
  if (( current_rc == 3 )); then return 0; fi
  (( current_rc == 0 )) || return "$current_rc"

  require_built_revision
  current_rc=0
  run_dev_test_matrix 1 || current_rc=$?
  if (( current_rc == 3 )); then return 0; fi
  return "$current_rc"
}

run_mainline() {
  run_release_qualification
}

release_selected_revision_current() {
  [[ "${LAPLACE_SKIP_IF_SUPERSEDED:-0}" == 1 ]] || return 0

  local selected latest freshness_rc=0
  selected="$(git rev-parse HEAD)"
  latest="$(git ls-remote --heads origin refs/heads/main | awk '{print $1}')"
  if [[ -z "$latest" ]]; then
    echo "::error::could not resolve current main while qualifying selected revision" >&2
    return 2
  fi
  if [[ "$latest" == "$selected" ]]; then return 0; fi

  if ! git cat-file -e "$latest^{commit}" 2>/dev/null; then
    git fetch --no-tags --depth=1 origin "$latest" >/dev/null 2>&1 || {
      echo "::error::could not fetch current main $latest for product-freshness comparison" >&2
      return 2
    }
  fi

  python3 scripts/ci-product-freshness.py --base "$selected" --head "$latest" || freshness_rc=$?
  if (( freshness_rc == 0 )); then
    echo "::notice::main advanced from $selected to product-equivalent $latest; candidate remains valid"
    return 0
  fi
  if (( freshness_rc == 3 )); then
    echo "::notice::selected revision $selected is superseded by product-changing main $latest"
    return 3
  fi
  echo "::error::could not determine product freshness for $selected against $latest" >&2
  return 2
}

release_candidate_current_before_mutation() {
  local current_rc=0
  release_selected_revision_current || current_rc=$?
  if (( current_rc != 3 )); then return "$current_rc"; fi

  local selected latest
  selected="$(git rev-parse HEAD)"
  latest="$(git ls-remote --heads origin refs/heads/main | awk '{print $1}')"
  echo "::notice::release candidate $selected became superseded by $latest before install/database mutation; mutation skipped"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      echo "## Superseded release candidate"
      echo
      echo "- Selected: $selected"
      echo "- Current main: $latest"
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
  run_publish
}

run_release_activation() {
  reconcile_installed_product
  run_live_tests
}

run_release_delivery() {
  check_deps
  carry_forward_undelivered_impact
  require_built_revision

  # Qualification is allowed to be superseded and cancelled. Delivery is not.
  # Recheck freshness at the mutation boundary; once current, finish the selected
  # delivery plan coherently even if a newer main revision appears.
  local current_rc=0
  release_candidate_current_before_mutation || current_rc=$?
  if (( current_rc == 3 )); then
    return 0
  fi
  (( current_rc == 0 )) || return "$current_rc"
  export LAPLACE_SKIP_IF_SUPERSEDED=0

  local actions="${LAPLACE_DELIVERY_ACTIONS:-all}"
  echo "::notice::delivery actions=$actions publish_scope=${LAPLACE_PUBLISH_SCOPE:-full} db_suites=${LAPLACE_DB_SUITES:-all} live_suites=${LAPLACE_LIVE_SUITES:-all}"

  if csv_selected "$actions" install; then
    run_install
  else
    echo "::notice::native installation remains valid; install skipped"
  fi

  if csv_selected "$actions" database; then
    run_database_maintenance --prepare
    run_db_tests
  else
    echo "::notice::database preparation/regression remains valid; database mutation skipped"
  fi

  # Every delivered source revision owns an exact application revision receipt.
  # The planner may choose API-only publication, but publication itself is never
  # omitted for a real product delivery.
  csv_selected "$actions" publish || {
    echo "::error::release-delivery plan omitted mandatory publication" >&2
    return 2
  }

  # Publication consumes the candidate qualified above. If web inputs changed,
  # require the sealed SPA artifact from this exact revision. If they did not,
  # preserve the installed SPA instead of rebuilding unrelated frontend work.
  if csv_selected "${LAPLACE_BUILD_COMPONENTS:-all}" web; then
    export LAPLACE_REQUIRE_QUALIFIED_WEB=1
    unset LAPLACE_REUSE_INSTALLED_WEB || true
  else
    export LAPLACE_REUSE_INSTALLED_WEB=1
    unset LAPLACE_REQUIRE_QUALIFIED_WEB || true
  fi
  run_publish

  if csv_selected "$actions" reconcile; then
    reconcile_installed_product
  else
    verify_installed_product
  fi

  csv_selected "$actions" live || {
    echo "::error::release-delivery plan omitted mandatory live verification" >&2
    return 2
  }
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
  release-delivery)
    run_release_delivery
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
