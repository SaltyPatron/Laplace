#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage="${1:-build}"
case "$stage" in
  provision|verify|chess-lab|reconcile|check|build|install|applications|deploy|proof|release-qualification|release-delivery|release-candidate|release-activation|proof-model|mainline|test-dev|test-db|test-live) ;;
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
    scripts/check-deployed-revision.sh \
    scripts/publish-applications.sh \
    deploy/linux/deploy.sh
  python3 -m py_compile \
    scripts/ci-impact-plan.py \
    scripts/ci-qualification-cache.py \
    scripts/ci-product-freshness.py \
    scripts/ci_product_scope.py \
    scripts/ci_managed_projects.py \
    scripts/web-artifact.py \
    scripts/atomic-directory-exchange.py
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
  python3 scripts/test-application-publish.py
  python3 scripts/test-cutechess-calibration.py
  python3 scripts/test-chess-x11-runtime.py
  python3 scripts/test-chess-floor-artifacts.py
  python3 scripts/test-recorded-chess-selection.py
  python3 scripts/test-chess-environment-benchmark.py ChessEnvironmentTests
  python3 scripts/test-managed-db-scheduling.py
  python3 scripts/test-codegen-configure.py
  python3 scripts/test-cmake-release.py
  python3 scripts/test-web-artifact.py
  python3 scripts/test-atomic-directory-exchange.py
  python3 scripts/test-web-publication.py
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
  source_sha="$(python3 scripts/ci-qualification-cache.py latest-source --suite native-dev 2>/dev/null || true)"
  [[ "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]] || return 1

  # A native test edit invalidates native-dev qualification, but it does not
  # change the runtime ELF/ROM consumed by managed projects. Reuse the latest
  # successfully qualified build only when its runtime/build inputs are byte-
  # equivalent to this candidate; test-only paths are deliberately excluded.
  git cat-file -e "$source_sha^{commit}" 2>/dev/null     || git fetch --no-tags --depth=1 origin "$source_sha" >/dev/null 2>&1     || return 1
  git diff --quiet "$source_sha" HEAD --     CMakeLists.txt cmake engine extension scripts/provision-cmake.py     ':(exclude)engine/**/tests/**'     ':(exclude)extension/**/tests/**'     || return 1

  work_root="${LAPLACE_WORK_ROOT:-/build/laplace/work}"
  source_root="$work_root/product-worktrees/$source_sha"
  [[ -f "$source_root/build/engine/core/liblaplace_core.so" ]] || return 1
  find -L "$source_root/build/engine" -name 'laplace_t0_perfcache*.bin' -print -quit | grep -q . || return 1

  mkdir -p build
  [[ ! -e build/engine && ! -L build/engine ]] || return 1
  ln -s "$source_root/build/engine" build/engine
  echo "::notice::reusing runtime-equivalent qualified native build from $source_sha"
  return 0
}

run_build() {
  local args=() selected="${LAPLACE_BUILD_COMPONENTS:-}"
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

  # The checked-in OpenAPI document is an explicit web build input. The
  # impact planner owns publication closure: full SPA publication selects the
  # four managed publish roots because deploy uses --no-build for those payloads,
  # while keeping managed test suites unscheduled when managed source is unchanged.

  # Managed compilation has no native link step. Native payload provenance is
  # selected later by publication: a native-invalidating plan supplies the
  # candidate build, while a managed-only plan preserves the installed native
  # closure byte-for-byte. Never turn a managed edit into a C++ rebuild here.
  if (( need_managed == 1 && need_native == 0 )); then
    export LAPLACE_REUSE_INSTALLED_NATIVE=1
  else
    unset LAPLACE_REUSE_INSTALLED_NATIVE || true
  fi

  local phases=()
  if (( need_native == 1 )); then
    if reuse_qualified_native_build; then
      need_native=0
    else
      [[ ! -L build/engine ]] || rm -f build/engine
      phases+=(build-native)
    fi
  fi
  (( need_managed == 0 )) || phases+=(build-app)
  (( need_web == 0 )) || phases+=(build-web)
  if csv_selected "${LAPLACE_DELIVERY_ACTIONS:-}" extension-sql; then
    phases+=(build-extension-sql)
  fi

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
  local current_rc profile suite spec cache_message record_message
  local selected="${LAPLACE_DEV_SUITES:-}"
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

  qualification_row() {
    local row_suite="$1" decision="$2" evidence="$3"
    [[ -n "${GITHUB_STEP_SUMMARY:-}" ]] || return 0
    evidence="${evidence//|/\\|}"
    printf '| `%s` | %s | %s |\n' "$row_suite" "$decision" "$evidence" >> "$GITHUB_STEP_SUMMARY"
  }

  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      echo "## Qualification execution"
      echo
      echo "- Selected development suites: ${selected:-none}"
      echo "- Receipt reuse: $([[ "$use_cache" == 1 ]] && echo enabled || echo disabled)"
      if [[ -n "${LAPLACE_MANAGED_TEST_PROJECTS:-}" ]]; then
        echo "- Managed test projects: ${LAPLACE_MANAGED_TEST_PROJECTS}"
      fi
      if [[ -n "${LAPLACE_MANAGED_TEST_FILTER:-}" ]]; then
        printf '%s%s%s\n' '- Managed test filter: `' "$LAPLACE_MANAGED_TEST_FILTER" '`'
      fi
      echo
      echo "| Suite | Decision | Evidence |"
      echo "| --- | --- | --- |"
    } >> "$GITHUB_STEP_SUMMARY"
  fi

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
      qualification_row "$suite" "unaffected" "impact planner did not invalidate this suite"
      continue
    fi

    cache_message=""
    if [[ "$use_cache" == 1 ]] && cache_message="$(python3 scripts/ci-qualification-cache.py check --suite "$suite")"; then
      echo "::notice::reusing passed qualification receipt for $suite"
      qualification_row "$suite" "reused" "$cache_message"
      continue
    fi

    current_rc=0
    bash scripts/test-parallel.sh --profile "$profile" --suite "$suite" || current_rc=$?
    if (( current_rc != 0 )); then
      qualification_row "$suite" "executed — failed" "test suite exited $current_rc"
      return "$current_rc"
    fi

    if [[ "$use_cache" == 1 ]]; then
      record_message="$(python3 scripts/ci-qualification-cache.py record \
        --suite "$suite" --source-sha "$(git rev-parse HEAD)")"
      qualification_row "$suite" "executed — passed" "$record_message"
    else
      qualification_row "$suite" "executed — passed" "receipt cache disabled for this operation"
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

run_install_extension_sql() {
  require_built_revision
  bash scripts/pipeline.sh install-extension-sql
}

run_install_ingest_runtime() {
  require_built_revision
  bash scripts/pipeline.sh install-ingest-runtime
}

run_database_maintenance() {
  bash scripts/maintain-installed-database.sh "$@"
}

csv_selected() {
  local selected="$1" wanted="$2"
  [[ "$selected" == all ]] || [[ -n "$selected" && ",$selected," == *",$wanted,"* ]]
}

force_full_carry_forward_impact() {
  # The application receipt owns only the managed application publication.
  # Missing app provenance must not erase already-selected web/native work or
  # manufacture database work from an unrelated stale receipt.
  append_csv_env LAPLACE_BUILD_COMPONENTS managed
  for project in \
    app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj \
    app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj \
    app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj \
    app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj \
    app/Laplace.Migrations/Laplace.Migrations.csproj; do
    append_csv_env LAPLACE_MANAGED_BUILD_PROJECTS "$project"
  done
  append_csv_env LAPLACE_DELIVERY_ACTIONS publish
  merge_publish_scope full
}

append_csv_env() {
  local name="$1" value="$2" current
  current="$(printenv "$name" 2>/dev/null || true)"
  [[ "$current" == all ]] && return 0
  [[ ",$current," == *",$value,"* ]] || {
    current="${current:+$current,}$value"
    printf -v "$name" '%s' "$current"
    export "$name"
  }
}

merge_publish_scope() {
  local incoming="$1" current="${LAPLACE_PUBLISH_SCOPE:-}"
  [[ -n "$incoming" ]] || return 0
  if [[ -z "$current" ]]; then
    export LAPLACE_PUBLISH_SCOPE="$incoming"
    return 0
  fi
  [[ "$current" != "$incoming" ]] || return 0
  case "$current:$incoming" in
    full:*|all:*|*:full|*:all|uci:*|*:uci)
      export LAPLACE_PUBLISH_SCOPE=full
      ;;
    api:web|web:api|api-web:*|*:api-web)
      export LAPLACE_PUBLISH_SCOPE=api-web
      ;;
    *)
      export LAPLACE_PUBLISH_SCOPE=full
      ;;
  esac
}

ensure_revision_available() {
  local revision="$1"
  git cat-file -e "$revision^{commit}" 2>/dev/null \
    || git fetch --no-tags --depth=1 origin "$revision"
}

force_web_carry_forward_impact() {
  # The SPA owns an isolated sealed-artifact transaction. A missing/stale installed
  # web receipt therefore requires only the web artifact and bounded live checks;
  # qualification that already passed is not widened and managed binaries stay valid.
  append_csv_env LAPLACE_BUILD_COMPONENTS web
  append_csv_env LAPLACE_DELIVERY_ACTIONS publish
  # Adding an unpublished SPA must not discard an already-selected API/native
  # closure. Otherwise installation advances the prefix while API stays stale.
  case "${LAPLACE_PUBLISH_SCOPE:-web}" in
    full|all|uci) export LAPLACE_PUBLISH_SCOPE=full ;;
    api|api-web) export LAPLACE_PUBLISH_SCOPE=api-web ;;
    *) export LAPLACE_PUBLISH_SCOPE=web ;;
  esac
}

carry_forward_installed_web_impact() {
  local target="$1" app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  local receipt="$app_dir/wwwroot/.laplace-web-source-revision"
  local deployed plan needs_web

  if ! csv_selected "${LAPLACE_BUILD_COMPONENTS:-}" web \
     && ! csv_selected "${LAPLACE_DELIVERY_ACTIONS:-}" publish; then
    return 0
  fi

  deployed="$(cat "$receipt" 2>/dev/null || true)"
  if [[ ! "$deployed" =~ ^[0-9a-fA-F]{40}$ ]]; then
    echo "::warning::installed SPA revision receipt is unavailable; forcing web rebuild/publication"
    force_web_carry_forward_impact
    return 0
  fi
  [[ "$deployed" != "$target" ]] || return 0

  if ! git cat-file -e "$deployed^{commit}" 2>/dev/null; then
    if ! git fetch --no-tags --depth=1 origin "$deployed"; then
      echo "::warning::could not resolve installed SPA revision $deployed; forcing web rebuild/publication"
      force_web_carry_forward_impact
      return 0
    fi
  fi

  if ! plan="$(python3 scripts/ci-impact-plan.py --root "$PWD" --base "$deployed" --head "$target")"; then
    echo "::warning::could not compute installed-SPA impact; forcing web rebuild/publication"
    force_web_carry_forward_impact
    return 0
  fi
  needs_web="$(CARRY_WEB_PLAN_JSON="$plan" python3 - <<'PY'
import json, os
plan = json.loads(os.environ["CARRY_WEB_PLAN_JSON"])
print("1" if "web" in plan.get("build_components", []) else "0")
PY
)"
  if [[ "$needs_web" == 1 ]]; then
    echo "::notice::installed SPA $deployed is behind web inputs at $target; carrying web publication forward"
    force_web_carry_forward_impact
  fi
}

carry_forward_installed_native_impact() {
  local target="$1" prefix="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
  local receipt="$prefix/lib/.laplace-source-revision"
  local deployed plan flags needs_native needs_database needs_reconcile needs_publish

  deployed="$(cat "$receipt" 2>/dev/null || true)"
  if [[ ! "$deployed" =~ ^[0-9a-fA-F]{40}$ ]]; then
    echo "::warning::native prefix revision receipt is unavailable; carrying only native install forward"
    append_csv_env LAPLACE_BUILD_COMPONENTS native
    append_csv_env LAPLACE_DELIVERY_ACTIONS install
    return 0
  fi
  [[ "$deployed" != "$target" ]] || return 0
  ensure_revision_available "$deployed" || {
    echo "::warning::could not resolve installed native revision $deployed; carrying native install forward"
    append_csv_env LAPLACE_BUILD_COMPONENTS native
    append_csv_env LAPLACE_DELIVERY_ACTIONS install
    return 0
  }

  plan="$(python3 scripts/ci-impact-plan.py --root "$PWD" --base "$deployed" --head "$target")" || return 1
  flags="$(CARRY_NATIVE_PLAN_JSON="$plan" python3 - <<'PY'
import json, os
p=json.loads(os.environ["CARRY_NATIVE_PLAN_JSON"])
native="native" in p.get("build_components", [])
a=set(p.get("delivery_actions", []))
print("1" if native else "0", "1" if native and "database" in a else "0",
      "1" if native and "reconcile" in a else "0", "1" if native and "publish" in a else "0")
PY
)"
  read -r needs_native needs_database needs_reconcile needs_publish <<< "$flags"
  [[ "$needs_native" == 1 ]] || return 0

  append_csv_env LAPLACE_BUILD_COMPONENTS native
  append_csv_env LAPLACE_DELIVERY_ACTIONS install
  [[ "$needs_database" != 1 ]] || append_csv_env LAPLACE_DELIVERY_ACTIONS database
  if [[ "$needs_reconcile" == 1 ]]; then
    echo "::notice::installed native diff requires explicit reconciliation; automatic delivery will not run it"
  fi
  if [[ "$needs_publish" == 1 ]]; then
    append_csv_env LAPLACE_DELIVERY_ACTIONS publish
    merge_publish_scope full
  fi
  echo "::notice::native carry-forward uses native receipt $deployed, not the application receipt"
}

carry_forward_installed_ingest_runtime_impact() {
  local target="$1" prefix="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
  local receipt="$prefix/ingest/current/.laplace-source-revision"
  local deployed plan needs_ingest

  deployed="$(cat "$receipt" 2>/dev/null || true)"
  if [[ ! "$deployed" =~ ^[0-9a-fA-F]{40}$ ]]; then
    echo "::warning::ingest runtime revision receipt is unavailable; carrying its bounded managed owner forward"
    append_csv_env LAPLACE_BUILD_COMPONENTS managed
    append_csv_env LAPLACE_MANAGED_BUILD_PROJECTS app/Laplace.Cli/Laplace.Cli.csproj
    append_csv_env LAPLACE_DELIVERY_ACTIONS ingest-runtime
    return 0
  fi
  [[ "$deployed" != "$target" ]] || return 0
  ensure_revision_available "$deployed" || {
    echo "::warning::could not resolve installed ingest-runtime revision $deployed; carrying its bounded managed owner forward"
    append_csv_env LAPLACE_BUILD_COMPONENTS managed
    append_csv_env LAPLACE_MANAGED_BUILD_PROJECTS app/Laplace.Cli/Laplace.Cli.csproj
    append_csv_env LAPLACE_DELIVERY_ACTIONS ingest-runtime
    return 0
  }
  plan="$(python3 scripts/ci-impact-plan.py --root "$PWD" --base "$deployed" --head "$target")" || {
    echo "::warning::could not compute installed ingest-runtime impact; carrying its bounded managed owner forward"
    append_csv_env LAPLACE_BUILD_COMPONENTS managed
    append_csv_env LAPLACE_MANAGED_BUILD_PROJECTS app/Laplace.Cli/Laplace.Cli.csproj
    append_csv_env LAPLACE_DELIVERY_ACTIONS ingest-runtime
    return 0
  }
  needs_ingest="$(CARRY_INGEST_PLAN_JSON="$plan" python3 - <<'PY'
import json, os
p=json.loads(os.environ["CARRY_INGEST_PLAN_JSON"])
print("1" if "ingest-runtime" in p.get("delivery_actions", []) else "0")
PY
)"
  [[ "$needs_ingest" == 1 ]] || return 0
  append_csv_env LAPLACE_BUILD_COMPONENTS managed
  append_csv_env LAPLACE_MANAGED_BUILD_PROJECTS app/Laplace.Cli/Laplace.Cli.csproj
  append_csv_env LAPLACE_DELIVERY_ACTIONS ingest-runtime
  echo "::notice::ingest-runtime carry-forward uses its immutable runtime receipt $deployed"
}

carry_forward_installed_extension_impact() {
  local prefix="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
  local manifest="$prefix/share/postgresql/18/extension/laplace_execution_module.txt"
  local rc=0

  python3 scripts/check-installed-extension-current.py \
    --execution-module-file "$manifest" >/dev/null 2>&1 || rc=$?
  if (( rc == 0 )); then
    return 0
  fi

  # rc=1 is known stale; rc=2 means provenance cannot be established. Both
  # require only extension SQL/database convergence, never an invented native rebuild.
  append_csv_env LAPLACE_DELIVERY_ACTIONS extension-sql
  append_csv_env LAPLACE_DELIVERY_ACTIONS database
  echo "::notice::extension SQL carry-forward selected from installed extension parity (status=$rc)"
}

carry_forward_installed_application_impact() {
  local target="$1" app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  local receipt="$app_dir/.laplace-source-revision"
  local deployed plan

  deployed="$(cat "$receipt" 2>/dev/null || true)"
  if [[ ! "$deployed" =~ ^[0-9a-fA-F]{40}$ ]]; then
    echo "::warning::installed application revision receipt is unavailable; carrying only managed application publication forward"
    force_full_carry_forward_impact
    return 0
  fi
  [[ "$deployed" != "$target" ]] || return 0
  ensure_revision_available "$deployed" || {
    echo "::warning::could not resolve installed application revision $deployed; carrying managed application publication forward"
    force_full_carry_forward_impact
    return 0
  }
  plan="$(python3 scripts/ci-impact-plan.py --root "$PWD" --base "$deployed" --head "$target")" || {
    force_full_carry_forward_impact
    return 0
  }

  eval "$(CARRY_APP_PLAN_JSON="$plan" python3 - <<'PY'
import json, os, shlex
p=json.loads(os.environ["CARRY_APP_PLAN_JSON"])
if "managed" not in p.get("build_components", []):
    raise SystemExit(0)

def merge_csv(name, incoming, order=None):
    current=os.environ.get(name, "")
    if current == "all" or "all" in incoming:
        value="all"
    else:
        values={x for x in current.split(",") if x}
        values.update(incoming)
        if order:
            value=",".join(x for x in order if x in values)
        else:
            value=",".join(sorted(values))
    print(f"export {name}={shlex.quote(value)}")

merge_csv("LAPLACE_BUILD_COMPONENTS", ["managed"], ("native","managed","web"))
field=("managed_delivery_build_projects"
       if os.environ.get("LAPLACE_STAGE","") in {"mainline","release-delivery","release-candidate","release-activation"}
       else "managed_build_projects")
merge_csv("LAPLACE_MANAGED_BUILD_PROJECTS", p.get(field, []))

actions=[]
if "publish" in p.get("delivery_actions", []):
    actions.append("publish")
changed=p.get("changed_files", [])
if any(x.startswith("db/") or x.startswith("app/Laplace.Migrations/") for x in changed):
    actions.append("database")
merge_csv("LAPLACE_DELIVERY_ACTIONS", actions,
          ("install","extension-sql","ingest-runtime","database","publish","live"))

scope=os.environ.get("LAPLACE_PUBLISH_SCOPE","")
incoming=p.get("publish_scope","api")
if "publish" in actions:
    if not scope:
        scope=incoming
    elif scope != incoming:
        if "full" in (scope,incoming) or "all" in (scope,incoming) or "uci" in (scope,incoming):
            scope="full"
        elif "api-web" in (scope,incoming) or {scope,incoming}=={"api","web"}:
            scope="api-web"
        else:
            scope="full"
    print(f"export LAPLACE_PUBLISH_SCOPE={shlex.quote(scope)}")
PY
)"
  echo "::notice::application carry-forward uses application receipt $deployed and cannot manufacture native/extension/web work"
}

carry_forward_undelivered_impact() {
  # Each installed component owns its own clock. A failed API publication must
  # not make a completed native/extension/database mutation look undelivered.
  case "${LAPLACE_STAGE:-}" in
    mainline|release-qualification|release-delivery) ;;
    *) return 0 ;;
  esac

  local target
  target="$(git rev-parse HEAD)"
  carry_forward_installed_web_impact "$target"
  carry_forward_installed_native_impact "$target"
  carry_forward_installed_ingest_runtime_impact "$target"
  carry_forward_installed_extension_impact
  carry_forward_installed_application_impact "$target"

  echo "::notice::component carry-forward target=$target build=${LAPLACE_BUILD_COMPONENTS:-} managed_build=${LAPLACE_MANAGED_BUILD_PROJECTS:-} delivery=${LAPLACE_DELIVERY_ACTIONS:-} publish=${LAPLACE_PUBLISH_SCOPE:-}"
}

run_db_tests() {
  require_built_revision
  local selected="${LAPLACE_DB_SUITES:-}"

  if csv_selected "$selected" db-health; then
    if csv_selected "${LAPLACE_BUILD_COMPONENTS:-}" native; then
      bash scripts/test-parallel.sh --profile db --suite db-health
    else
      LAPLACE_DB_HEALTH_SCOPE=installed \
        bash scripts/test-parallel.sh --profile db --suite db-health
    fi
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
  (
  local scope="${LAPLACE_PUBLISH_SCOPE:-full}"
  local base="${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}"
  local api_key="${LAPLACE_API_KEY:-}" issued_prefix=""

  # Publication consumes the install-final native closure. CMake installation
  # finalizes RPATHs, so pre-install build-tree ELF bytes are not a deployable
  # identity even when they came from the same qualified source revision.
  export LAPLACE_REUSE_INSTALLED_NATIVE=1

  case "$scope" in
    api|api-web|full|all)
      if [[ -z "$api_key" ]]; then
        IFS=$'\t' read -r api_key issued_prefix < <(issue_live_proof_credential "$base")
      fi
      [[ -n "$api_key" ]] || {
        echo "::error::could not obtain the bounded publication-verification credential" >&2
        return 1
      }
      export LAPLACE_API_KEY="$api_key"
      trap 'revoke_live_proof_credential "$base" "$api_key" "$issued_prefix"' EXIT
      ;;
  esac

  case "$scope" in
    web) bash scripts/publish-applications.sh web-recover ;;
    api) bash scripts/publish-applications.sh api-recover ;;
    api-web)
      bash scripts/publish-applications.sh api-recover
      bash scripts/publish-applications.sh web-recover
      ;;
    uci) bash scripts/publish-applications.sh uci-recover ;;
    full|all) bash scripts/publish-applications.sh recover ;;
    *)
      echo "::error::unknown publication scope: $scope" >&2
      return 2
      ;;
  esac

  require_built_revision
  case "$scope" in
    web) bash scripts/publish-applications.sh web-deploy ;;
    api) bash scripts/publish-applications.sh api-deploy ;;
    api-web)
      bash scripts/publish-applications.sh api-deploy
      bash scripts/publish-applications.sh web-deploy
      ;;
    uci) bash scripts/publish-applications.sh uci-deploy ;;
    full|all) bash scripts/publish-applications.sh deploy ;;
  esac
  )
}

verify_isolated_uci_delivery() {
  require_deployed_revision
  python3 - "$ROOT/build/.uci-publish-verified.json" <<'PY'
import json
import pathlib
import sys
path = pathlib.Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
assert value["schema"] == "laplace.uci-payload-verification/v1", value
assert value["status"] == "passed", value
assert value["bestmove"], value
assert value["substrate_access_verified"] is False, value
PY
}

verify_isolated_web_delivery() {
  require_deployed_revision
  python3 scripts/web-artifact.py verify-installed \
    --root "$ROOT" \
    --manifest "$ROOT/build/.laplace-web-artifact.json" \
    --directory "${LAPLACE_APP_DIR:-/opt/laplace/app}/wwwroot"
  check_application_live
}

verify_isolated_api_delivery() {
  require_deployed_revision
  local receipt="$ROOT/build/.api-publish-verified.json"
  jq -e '
    .schema == "laplace.api-payload-verification/v1" and
    .status == "passed" and
    (.payload | type == "object") and
    (.service.ActiveState == "active") and
    (.service.SubState == "running")
  ' "$receipt" >/dev/null || {
    echo "::error::API-only publication receipt is missing or invalid: $receipt" >&2
    return 1
  }
  check_application_live
}
run_live_tests() {
  require_deployed_revision
  local selected="${LAPLACE_LIVE_SUITES:-}"
  export LAPLACE_API_BASE="${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}"
  export LAPLACE_PUBLIC_UI_BASE="${LAPLACE_PUBLIC_UI_BASE:-http://127.0.0.1:8080}"

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
  if csv_selected "$selected" chess-provider-live; then
    bash scripts/test-parallel.sh --profile live --suite chess-provider-live
  else
    echo "::notice::live planner kept chess-provider-live valid; suite not scheduled"
  fi
}
verify_installed_web_receipt() {
  local target app_dir receipt deployed plan needs_web
  target="$(git rev-parse HEAD)"
  app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  receipt="$app_dir/wwwroot/.laplace-web-source-revision"
  deployed="$(cat "$receipt" 2>/dev/null || true)"
  [[ "$deployed" =~ ^[0-9a-fA-F]{40}$ ]] || {
    echo "::error::installed SPA has no valid source revision receipt: $receipt" >&2
    return 1
  }
  [[ "$deployed" != "$target" ]] || return 0
  git cat-file -e "$deployed^{commit}" 2>/dev/null || git fetch --no-tags --depth=1 origin "$deployed"
  plan="$(python3 scripts/ci-impact-plan.py --root "$PWD" --base "$deployed" --head "$target")" || return 1
  needs_web="$(WEB_RECEIPT_PLAN_JSON="$plan" python3 - <<'PY'
import json, os
plan = json.loads(os.environ["WEB_RECEIPT_PLAN_JSON"])
print("1" if "web" in plan.get("build_components", []) else "0")
PY
)"
  [[ "$needs_web" == 0 ]] || {
    echo "::error::installed SPA revision $deployed is stale relative to delivered source $target" >&2
    return 1
  }
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

issue_live_proof_credential() {
  local api_base="$1" operator_token="${LAPLACE_OPERATOR_TOKEN:-}" issued_json
  local deadline=$((SECONDS + 60))
  if [[ -z "$operator_token" && -r /opt/laplace/secrets/operator.env ]]; then
    operator_token="$(
      set -a
      # shellcheck disable=SC1091
      source /opt/laplace/secrets/operator.env
      printf '%s' "${LAPLACE_OPERATOR_TOKEN:-}"
    )"
  fi
  [[ -n "$operator_token" ]] || {
    echo "::error::live verification requires LAPLACE_API_KEY or LAPLACE_OPERATOR_TOKEN" >&2
    return 1
  }
  # systemctl start returns when the process is launched, not when Kestrel has
  # bound the socket and completed startup. Publication immediately follows a
  # PostgreSQL/API restart, so wait at this exact boundary instead of turning a
  # normal two-second startup into a failed deployment.
  until curl -fsS "$api_base/health" >/dev/null 2>&1; do
    if (( SECONDS >= deadline )); then
      echo "::error::installed laplace-api did not become ready for publication verification within 60 seconds" >&2
      return 1
    fi
    sleep 1
  done
  issued_json="$(curl -fsS -X POST "$api_base/v1/billing/operator/keys" \
    -H 'Content-Type: application/json' \
    -H "X-Laplace-Operator-Token: $operator_token" \
    --data '{"tenant":"local-dev","label":"installed-product-verification"}')" || {
      echo "::error::could not issue the bounded live-verification credential" >&2
      return 1
    }
  ISSUED_JSON="$issued_json" python3 - <<'PY'
import json
import os

issued = json.loads(os.environ["ISSUED_JSON"])
print(issued["api_key"], issued["key_prefix"], sep="\t")
PY
}

revoke_live_proof_credential() {
  local api_base="$1" api_key="$2" key_prefix="$3"
  [[ -n "$key_prefix" ]] || return 0
  curl -fsS -X POST "$api_base/v1/billing/keys/revoke" \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $api_key" \
    --data "{\"key_prefix\":\"$key_prefix\"}" >/dev/null
}

check_t0_perfcache_runtime() {
  local host="${PGHOST:-/var/run/postgresql}"
  local user="${PGUSER:-laplace_admin}"
  local database="${PGDATABASE:-laplace}"
  local word_id db_receipt perfcache_path file_receipt api_key="${LAPLACE_API_KEY:-}"
  local api_base="${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}"
  local -a proof_headers=(-H 'Content-Type: application/json')

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

  [[ -n "$api_key" ]] || {
    echo "::error::live storage proof requires the bounded verification credential" >&2
    return 1
  }
  proof_headers+=(-H "Authorization: Bearer $api_key")

  local proof
  proof="$(curl -fsS -X POST "$api_base/v1/explore/storage-proof" \
    "${proof_headers[@]}" \
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
  unset api_key
  echo "::notice::storage-proof endpoint verified against live T0 ROM"
}

verify_installed_product() (
  local base="${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}"
  local expected="${1:-$(git rev-parse HEAD)}"
  local api_key="${LAPLACE_API_KEY:-}" issued_prefix=""
  if [[ -z "$api_key" ]]; then
    IFS=$'\t' read -r api_key issued_prefix < <(issue_live_proof_credential "$base")
  fi
  [[ -n "$api_key" ]] || {
    echo "::error::could not obtain the bounded live-verification credential" >&2
    return 1
  }
  export LAPLACE_API_KEY="$api_key"
  cleanup_live_proof_credential() {
    revoke_live_proof_credential "$base" "$api_key" "$issued_prefix"
  }
  trap cleanup_live_proof_credential EXIT
  bash scripts/check-deployed-revision.sh "$expected"
  if [[ "${LAPLACE_REUSE_INSTALLED_NATIVE:-0}" == 1 ]]; then
    # Managed-only publication deliberately has no source-native build tree.
    # Validate the live catalog against the independently receipted installed
    # extension instead of demanding a manifest that this plan did not build.
    bash scripts/check-database-health.sh --installed-runtime "${PGDATABASE:-laplace}"
  else
    bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"
  fi
  check_application_live
  verify_installed_web_receipt
  check_t0_perfcache_runtime
  # This verifies the application process. The managed-host owner separately
  # verifies the public HTTP->HTTPS redirect, certificate, and reverse proxy.
  python3 scripts/verify-application-release.py --base "$base" --timeout-seconds 60
)

verify_current_installed_product() {
  local receipt="${LAPLACE_APP_DIR:-/opt/laplace/app}/.laplace-source-revision" expected
  expected="$(cat "$receipt" 2>/dev/null || true)"
  [[ "$expected" =~ ^[0-9a-f]{40}$ ]] || {
    echo "::error::installed application revision receipt is missing or invalid: $receipt" >&2
    return 1
  }
  LAPLACE_REUSE_INSTALLED_NATIVE=1 verify_installed_product "$expected"
}

run_chess_lab() {
  check_deps
  bash scripts/pipeline.sh chess-lab
  sudo -n systemctl restart laplace-api
  check_application_live
  python3 scripts/check-chess-dependencies.py --cutechess-gui
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

  # Execute only the suites invalidated by the changed implementation paths.
  # Project and class selectors are supplied by the impact planner; unaffected
  # suites remain represented by their existing qualification receipts.
  current_rc=0
  run_dev_test_matrix 1 || current_rc=$?
  if (( current_rc == 3 )); then return 0; fi
  (( current_rc == 0 )) || return "$current_rc"

  current_rc=0
  release_selected_revision_current || current_rc=$?
  if (( current_rc == 3 )); then return 0; fi
  (( current_rc == 0 )) || return "$current_rc"

  require_built_revision
  return 0
}

run_mainline() {
  check_deps
  carry_forward_undelivered_impact

  # Main is the development deployment lane. Compile the exact affected
  # closure, deploy it in the same candidate reservation, and prove the
  # installed surfaces. Broader development/integration suites are explicit
  # audit operations; they do not hold deployment behind a second job.
  local current_rc=0
  release_selected_revision_current || current_rc=$?
  if (( current_rc == 3 )); then return 0; fi
  (( current_rc == 0 )) || return "$current_rc"

  run_build
  require_built_revision

  # Building the candidate is isolated by product-$TARGET_SHA.lock and never needs
  # the installed host/database. Wait for an ingest/maintenance owner only AFTER
  # compilation, at the delivery boundary that can mutate or publish installed state.
  (
    local work_root="${LAPLACE_WORK_ROOT:-/build/laplace/work}"
    mkdir -p "$work_root"
    exec 9>"$work_root/host-resource.lock"
    flock 9
    run_release_delivery
  )
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

run_release_mutation_window() (
  local actions="$1"
  local api_was_active=0 mutation_rc=0
  local requires_quiet=0

  if csv_selected "$actions" install \
     || csv_selected "$actions" extension-sql \
     || csv_selected "$actions" database; then
    requires_quiet=1
    bash scripts/wait-for-quiet-substrate.sh "${PGDATABASE:-laplace}"
  fi

  # An immutable ingest-runtime publication only creates a revision-addressed
  # directory and atomically advances ingest/current. A running ingest retains a
  # shared lease on its old directory, so this operation neither stops it nor
  # requires an API outage.
  if csv_selected "$actions" ingest-runtime && ! csv_selected "$actions" install; then
    run_install_ingest_runtime
  fi

  if (( requires_quiet == 0 )); then
    echo "::notice::no installed database/native mutation required"
    return 0
  fi

  systemctl is-active --quiet laplace-api 2>/dev/null && api_was_active=1 || true
  cleanup_release_mutation_window() {
    mutation_rc=$?
    trap - EXIT
    if [[ "$api_was_active" == 1 ]]; then
      sudo -n systemctl start laplace-api || mutation_rc=1
    fi
    exit "$mutation_rc"
  }
  trap cleanup_release_mutation_window EXIT
  [[ "$api_was_active" != 1 ]] || sudo -n systemctl stop laplace-api

  if csv_selected "$actions" install; then
    run_install
  else
    echo "::notice::native installation remains valid; install skipped"
  fi

  if csv_selected "$actions" extension-sql; then
    run_install_extension_sql
  else
    echo "::notice::extension SQL/control installation remains valid; SQL install skipped"
  fi

  if csv_selected "$actions" database; then
    run_database_maintenance --prepare
  else
    echo "::notice::database preparation remains valid; database mutation skipped"
  fi

  if [[ "$api_was_active" == 1 ]]; then
    sudo -n systemctl start laplace-api
    api_was_active=0
  fi
  trap - EXIT
)

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

  run_release_mutation_window "install,database"
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

  # Every main revision enters this owner. If the installed->target reconciliation
  # found no product mutation, finish without touching the host. This is distinct
  # from defaulting an empty selector to "all".
  local actions="${LAPLACE_DELIVERY_ACTIONS:-}"
  if [[ -z "$actions" ]]; then
    echo "::notice::installed product is current for all product-relevant inputs; delivery no-op"
    return 0
  fi

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

  local publish_scope="${LAPLACE_PUBLISH_SCOPE:-full}"
  echo "::notice::delivery actions=$actions publish_scope=$publish_scope db_suites=${LAPLACE_DB_SUITES:-} live_suites=${LAPLACE_LIVE_SUITES:-}"

  # Database-only and managed/web deliveries intentionally reuse the last
  # independently qualified native artifact. Make that ownership explicit
  # before the mutation window: db-health runs after migrations and must inspect
  # the installed extension rather than demand a native build manifest that this
  # plan correctly did not produce.
  if csv_selected "${LAPLACE_BUILD_COMPONENTS:-}" native; then
    unset LAPLACE_REUSE_INSTALLED_NATIVE || true
    unset LAPLACE_DB_HEALTH_SCOPE || true
  else
    export LAPLACE_REUSE_INSTALLED_NATIVE=1
    export LAPLACE_DB_HEALTH_SCOPE=installed
  fi

  run_release_mutation_window "$actions"

  # Automatic main delivery does not run integration qualification here.
  # run_database_maintenance --prepare already finishes with the bounded installed
  # database-health readback. native-db/managed-db belong to the explicit test-db
  # or audit lanes; they must never sit between a successful mutation and publish.
  if csv_selected "$actions" database; then
    echo "::notice::database mutation completed with health readback; integration suites remain explicit test-db/audit work"
  fi

  # Publication is a planner action, not a tax on native-only SHAs. pipeline.sh
  # install + postgres bounce does not republish API/MCP/UI.
  if csv_selected "$actions" publish; then
    if csv_selected "${LAPLACE_BUILD_COMPONENTS:-}" web; then
      export LAPLACE_REQUIRE_QUALIFIED_WEB=1
      unset LAPLACE_REUSE_INSTALLED_WEB || true
    else
      export LAPLACE_REUSE_INSTALLED_WEB=1
      unset LAPLACE_REQUIRE_QUALIFIED_WEB || true
    fi
    run_publish
  else
    echo "::notice::application publication omitted by planner"
  fi

  if csv_selected "$actions" reconcile && [[ "${LAPLACE_STAGE:-}" != mainline ]]; then
    reconcile_installed_product
  elif csv_selected "$actions" reconcile; then
    echo "::notice::automatic main delivery does not run corpus-wide reconciliation; use explicit reconcile/database maintenance"
    verify_installed_product
  elif csv_selected "$actions" publish; then
    if [[ "$publish_scope" == uci ]]; then
      verify_isolated_uci_delivery
    elif [[ "$publish_scope" == web ]]; then
      verify_isolated_web_delivery
    elif [[ "$publish_scope" == api ]]; then
      verify_isolated_api_delivery
    elif [[ "$publish_scope" == api-web ]]; then
      verify_isolated_api_delivery
      verify_isolated_web_delivery
    else
      verify_installed_product
    fi
  else
    echo "::notice::application verification omitted; planner did not publish"
  fi

  echo "::notice::full live suites are not part of automatic delivery; use the explicit test-live operation"
}
run_proof_model() {
  require_built_revision
  LAPLACE_MODEL_PROOF_CODE_CORPORA=1 bash scripts/model-synthesize-ci.sh
}

run_deploy() {
  # Explicit maintenance enters without an impact plan. Select the recovery
  # closure, qualify it, then let the same action-aware delivery owner decide
  # which install/database/publication/live operations are actually required.
  force_full_carry_forward_impact
  run_release_qualification
  run_release_delivery
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
  verify)
    check_deps
    verify_current_installed_product
    ;;
  chess-lab)
    run_chess_lab
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
