#!/usr/bin/env bash
# Profiled, change-aware test orchestration for Linux.
#
# Usage:
#   scripts/test-parallel.sh                # native DEV + regress QA, managed DEV + DB QA
#   scripts/test-parallel.sh --engine       # DEV/BAT: native engine + managed non-DB tests
#   scripts/test-parallel.sh --regress      # QA: pg_regress only
#   scripts/test-parallel.sh --app          # managed DEV/BAT, then managed DB QA
#   scripts/test-parallel.sh --app-dev      # managed DEV/BAT only
#   scripts/test-parallel.sh --app-db       # managed database QA only
#   scripts/test-parallel.sh --app-live     # seeded/shared product acceptance only
#   scripts/test-parallel.sh --integration  # DB health, then pg_regress || managed DB QA
#   scripts/test-parallel.sh --serial       # force serial ctest/profile execution
#   scripts/test-parallel.sh --all          # ignore fingerprint stamps
#
# The executable boundary is intentional:
#   DEV/BAT  = native non-regress + managed tests that own no database/product state
#   DB QA    = installed PostgreSQL/extension behavior and disposable DB fixtures
#   LIVE     = seeded shared substrate/product behavior
#   PERF     = explicit benchmark lane
#
# A profile is selected while EXECUTING tests, not merely checked afterward.
# This prevents the QA lane from inheriting every unit/contract test registered in
# the build and prevents a fresh healthy database from being judged by conversational
# behavior that requires lexical/knowledge seeds.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# shellcheck source=scripts/lib/fp.sh
source "$ROOT/scripts/lib/fp.sh"

MODE=all
SERIAL="${LAPLACE_TEST_SERIAL:-0}"

# These four sets are deliberately disjoint. Untagged managed tests are DEV/BAT.
# Tier=db tests must own/create their database fixture state and may rely on the
# installed extension, but not on a particular seeded corpus. Tier=live is the
# standing shared product substrate. Tier=perf is never inferred from wall-clock
# behavior on a busy runner.
DOTNET_DEV_FILTER='Tier!=db&Tier!=live&Tier!=perf'
DOTNET_DB_FILTER='Tier=db'
DOTNET_LIVE_FILTER='Tier=live'

while [[ $# -gt 0 ]]; do
  case "$1" in
    --engine)      MODE=engine; shift ;;
    --regress)     MODE=regress; shift ;;
    --app)         MODE=app; shift ;;
    --app-dev)     MODE=app-dev; shift ;;
    --app-db)      MODE=app-db; shift ;;
    --app-live)    MODE=app-live; shift ;;
    --integration) MODE=integration; shift ;;
    --serial)      SERIAL=1; shift ;;
    --all)         export LAPLACE_FORCE_ALL=1; shift ;;
    -h|--help)
      sed -n '2,27p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

nproc_n="$(nproc 2>/dev/null || echo 1)"
if [[ -z "${CMAKE_BUILD_PARALLEL_LEVEL:-}" ]]; then
  export CMAKE_BUILD_PARALLEL_LEVEL="$nproc_n"
fi
if [[ -z "${CTEST_PARALLEL_LEVEL:-}" ]]; then
  if [[ "$SERIAL" == "1" ]]; then
    export CTEST_PARALLEL_LEVEL=1
  else
    export CTEST_PARALLEL_LEVEL="$nproc_n"
  fi
fi

# DEV/BAT must execute the native artifacts built from this tree. App-local copies
# are refreshed by Directory.Build.props; LD_LIBRARY_PATH is the second line of
# defence for invocations that do not load from the app base directory.
export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"

PYTHON="$(command -v python3 || command -v python)"

run_ctest_engine() {
  local fp
  fp=$(fp_native)
  if fp_check test-engine "$fp"; then
    echo "==== native DEV/BAT skipped — engine unchanged since last pass (fp ${fp:0:12}) ===="
    return 0
  fi
  echo "==== native DEV/BAT: ctest -LE regress -j ${CTEST_PARALLEL_LEVEL} ===="
  local rc=0
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -LE regress || rc=$?
  if [[ "$rc" -eq 0 ]]; then fp_record test-engine "$fp"; fi
  return "$rc"
}

run_ctest_regress() {
  # pg_regress exercises the installed extension in its own disposable database.
  local fp
  fp="$(fp_native):$(cat "$FP_STAMP_DIR/install-native" 2>/dev/null || echo uninstalled)"
  if fp_check test-regress "$fp"; then
    echo "==== PostgreSQL QA skipped — engine/extension + install unchanged since last pass ===="
    return 0
  fi
  echo "==== PostgreSQL QA: ctest -L regress -j ${CTEST_PARALLEL_LEVEL} ===="
  local rc=0
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -L regress || rc=$?
  if [[ "$rc" -eq 0 ]]; then fp_record test-regress "$fp"; fi
  return "$rc"
}

set_perfcache_from_build() {
  local bin=""
  bin=$(find "$ROOT/build" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1 || true)
  if [[ -n "$bin" ]]; then
    export LAPLACE_PERFCACHE_BIN="$bin"
    echo "LAPLACE_PERFCACHE_BIN=$bin (built tree)"
  fi
}

set_perfcache_from_runtime() {
  local bin=""
  bin=$(find "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/share/laplace" "$ROOT/build" \
    -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1 || true)
  if [[ -n "$bin" ]]; then
    export LAPLACE_PERFCACHE_BIN="$bin"
    echo "LAPLACE_PERFCACHE_BIN=$bin (runtime QA)"
  fi
}

run_dotnet_dev() {
  set_perfcache_from_build

  # The project graph supplies app-source fingerprints. Add the native source state,
  # the executable profile, and this runner's content so a filter/orchestration change
  # cannot reuse a pass recorded for a different test set.
  local runner_fp salt plan_out plan_rc=0
  runner_fp=$(sha256sum "$ROOT/scripts/test-parallel.sh" | cut -d' ' -f1)
  salt="$(fp_native):profile=dev:filter=$DOTNET_DEV_FILTER:runner=$runner_fp"
  plan_out=$("$PYTHON" "$ROOT/scripts/affected-app.py" plan --ns test --salt "$salt") || plan_rc=$?
  if [[ "$plan_rc" -ne 0 ]]; then
    echo "::warning::affected-app DEV/BAT plan failed (rc=$plan_rc) — full solution test"
    ( cd "$ROOT/app" && dotnet test Laplace.slnx -c Release --nologo --verbosity minimal \
        --filter "$DOTNET_DEV_FILTER" )
    return $?
  fi
  if [[ -z "$plan_out" ]]; then
    echo "==== managed DEV/BAT skipped — no affected test project since last pass ===="
    return 0
  fi

  local -a projs=()
  mapfile -t projs <<<"$plan_out"
  if (( ${#projs[@]} > 6 )); then
    echo "==== managed DEV/BAT (${#projs[@]} affected — full solution) ===="
    local rc=0
    ( cd "$ROOT/app" && dotnet test Laplace.slnx -c Release --nologo --verbosity minimal \
        --filter "$DOTNET_DEV_FILTER" ) || rc=$?
    if [[ "$rc" -eq 0 ]]; then
      "$PYTHON" "$ROOT/scripts/affected-app.py" record --ns test --salt "$salt"
    fi
    return "$rc"
  fi

  echo "==== managed DEV/BAT (${#projs[@]} affected project(s)) ===="
  local p name rc=0
  local -a passed=()
  for p in "${projs[@]}"; do
    echo "---- dotnet DEV/BAT $p ----"
    if ( cd "$ROOT/app" && dotnet test "$p" -c Release --nologo --verbosity minimal \
           --filter "$DOTNET_DEV_FILTER" ); then
      name="${p##*/}"
      passed+=("${name%.csproj}")
    else
      rc=1
    fi
  done
  if [[ ${#passed[@]} -gt 0 ]]; then
    "$PYTHON" "$ROOT/scripts/affected-app.py" record --ns test --salt "$salt" \
      --projects "${passed[@]}"
  fi
  return "$rc"
}

run_dotnet_db() {
  # Always execute the DB profile after db-ops. Database identity/state is runtime
  # input that is intentionally outside the source fingerprint; a freshly recreated
  # database must not inherit a test stamp from the database that was dropped.
  set_perfcache_from_runtime
  echo "==== managed database QA: full solution, $DOTNET_DB_FILTER ===="
  ( cd "$ROOT/app" && dotnet test Laplace.slnx -c Release --nologo --verbosity minimal \
      --filter "$DOTNET_DB_FILTER" )
}

run_dotnet_live() {
  set_perfcache_from_runtime
  echo "==== seeded/shared product acceptance: full solution, $DOTNET_LIVE_FILTER ===="
  ( cd "$ROOT/app" && dotnet test Laplace.slnx -c Release --nologo --verbosity minimal \
      --filter "$DOTNET_LIVE_FILTER" )
}

run_database_health() {
  echo "==== seed-independent database health ===="
  bash "$ROOT/scripts/check-database-health.sh" "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
}

parallel_pair() {
  # parallel_pair <label-a> <fn-a> <label-b> <fn-b> — run both, print both logs,
  # fail if either failed.
  local la="$1" fa="$2" lb="$3" fb="$4"
  local log_a log_b pid_a pid_b rc_a rc_b
  log_a="$(mktemp)"; log_b="$(mktemp)"
  set +e
  "$fa" >"$log_a" 2>&1 &
  pid_a=$!
  "$fb" >"$log_b" 2>&1 &
  pid_b=$!
  wait "$pid_a"; rc_a=$?
  wait "$pid_b"; rc_b=$?
  set -e
  echo "---- $la ----"; cat "$log_a"
  echo "---- $lb ----"; cat "$log_b"
  rm -f "$log_a" "$log_b"
  if [[ "$rc_a" -ne 0 || "$rc_b" -ne 0 ]]; then
    echo "::error::test layer failed (${la}_rc=$rc_a ${lb}_rc=$rc_b)"
    return 1
  fi
}

case "$MODE" in
  engine)
    run_ctest_engine
    run_dotnet_dev
    echo "==== DEV/BAT OK ===="
    exit 0
    ;;
  regress)
    run_ctest_regress
    exit $?
    ;;
  app)
    run_dotnet_dev
    run_database_health
    run_dotnet_db
    exit 0
    ;;
  app-dev)
    run_dotnet_dev
    exit $?
    ;;
  app-db)
    run_database_health
    run_dotnet_db
    exit $?
    ;;
  app-live)
    run_dotnet_live
    exit $?
    ;;
  integration)
    run_database_health
    if [[ "$SERIAL" == "1" ]]; then
      run_ctest_regress
      run_dotnet_db
    else
      echo "==== QA parallel: PostgreSQL regress || managed DB fixtures ===="
      parallel_pair postgres-regress run_ctest_regress managed-db run_dotnet_db
    fi
    echo "==== database QA OK ===="
    exit 0
    ;;
esac

# Full local gate: native DEV and PostgreSQL regress may run together; managed DEV
# follows so it sees an uncontended built tree, then DB QA proves the runtime.
if [[ "$SERIAL" == "1" ]]; then
  run_ctest_engine
  run_dotnet_dev
  run_database_health
  run_ctest_regress
  run_dotnet_db
  exit 0
fi

echo "==== parallel: native DEV || PostgreSQL regress ===="
parallel_pair native-dev run_ctest_engine postgres-regress run_ctest_regress
run_dotnet_dev
run_database_health
run_dotnet_db
echo "==== test-parallel OK ===="
