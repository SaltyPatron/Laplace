#!/usr/bin/env bash
# Parallel, change-aware test orchestration for Linux (parity with scripts/win/test-all.cmd).
#
# Usage:
#   scripts/test-parallel.sh                # ctest (excl. regress) || regress, then dotnet
#   scripts/test-parallel.sh --engine       # ctest -LE regress only
#   scripts/test-parallel.sh --regress      # ctest -L regress only
#   scripts/test-parallel.sh --app          # dotnet test only
#   scripts/test-parallel.sh --integration  # regress || dotnet (CI integration job)
#   scripts/test-parallel.sh --serial       # force serial (or set LAPLACE_TEST_SERIAL=1)
#   scripts/test-parallel.sh --all          # ignore fingerprint stamps (LAPLACE_FORCE_ALL=1)
#
# Change-aware: each layer is gated on a content fingerprint (scripts/lib/fp.sh)
# and skipped when its inputs are unchanged since the last PASSING run of that
# layer. dotnet tests run only the affected ProjectReference closure
# (scripts/affected-app.py), salted with the native engine + migrations state so
# an engine or schema change re-proves the app layer even with no C# diff.
#
# Env:
#   CTEST_PARALLEL_LEVEL / CMAKE_BUILD_PARALLEL_LEVEL — from nproc if unset
#   LAPLACE_TEST_SERIAL=1 — serial ctest + serial layers
#   LAPLACE_FORCE_ALL=1 — run everything regardless of stamps

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# shellcheck source=scripts/lib/fp.sh
source "$ROOT/scripts/lib/fp.sh"

MODE=all
SERIAL="${LAPLACE_TEST_SERIAL:-0}"
# Tier=db RUNS. These tests may require PostgreSQL, but they must create/own their
# fixture state rather than depend on a particular seeded product corpus.
#
# Tier=live is different: it exercises the standing shared substrate/product and
# may require foundation/knowledge seeds or mutate durable cells. Those tests are
# valuable product acceptance probes, but they are not database-health tests and
# must not make migrate/integration red while a fresh DB is legitimately unseeded.
#
# Tier=perf remains explicit because runner load is not a stable performance oracle.
DOTNET_FILTER='Tier!=perf&Tier!=live'

while [[ $# -gt 0 ]]; do
  case "$1" in
    --engine)      MODE=engine; shift ;;
    --regress)     MODE=regress; shift ;;
    --app)         MODE=app; shift ;;
    --integration) MODE=integration; shift ;;
    --serial)      SERIAL=1; shift ;;
    --all)         export LAPLACE_FORCE_ALL=1; shift ;;
    -h|--help)
      sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'
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

export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"

PYTHON="$(command -v python3 || command -v python)"

run_ctest_engine() {
  local fp
  fp=$(fp_native)
  if fp_check test-engine "$fp"; then
    echo "==== ctest (excl. regress) skipped — engine unchanged since last pass (fp ${fp:0:12}) ===="
    return 0
  fi
  echo "==== ctest (excl. regress) -j ${CTEST_PARALLEL_LEVEL} ===="
  local rc=0
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -LE regress || rc=$?
  if [[ "$rc" -eq 0 ]]; then fp_record test-engine "$fp"; fi
  return "$rc"
}

run_ctest_regress() {
  local fp
  fp="$(fp_native):$(cat "$FP_STAMP_DIR/install-native" 2>/dev/null || echo uninstalled)"
  if fp_check test-regress "$fp"; then
    echo "==== ctest (regress) skipped — engine/extension + install unchanged since last pass ===="
    return 0
  fi
  echo "==== ctest (regress) -j ${CTEST_PARALLEL_LEVEL} ===="
  local rc=0
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -L regress || rc=$?
  if [[ "$rc" -eq 0 ]]; then fp_record test-regress "$fp"; fi
  return "$rc"
}

run_dotnet() {
  local bin=""
  bin=$(find "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/share/laplace" "$ROOT/build" \
    -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1 || true)
  if [[ -n "$bin" ]]; then
    export LAPLACE_PERFCACHE_BIN="$bin"
    echo "LAPLACE_PERFCACHE_BIN=$bin"
  fi

  local salt plan_out plan_rc=0
  salt="$(fp_runtime):$(cat "$FP_STAMP_DIR/install-native" 2>/dev/null || echo uninstalled)"
  plan_out=$("$PYTHON" "$ROOT/scripts/affected-app.py" plan --ns test --salt "$salt") || plan_rc=$?
  if [[ "$plan_rc" -ne 0 ]]; then
    echo "::warning::affected-app plan failed (rc=$plan_rc) — full solution test"
    ( cd "$ROOT/app" && dotnet test Laplace.slnx -c Release --nologo --verbosity minimal \
        --filter "$DOTNET_FILTER" )
    return 0
  fi
  if [[ -z "$plan_out" ]]; then
    echo "==== dotnet test skipped — no affected test project since last pass ===="
    return 0
  fi

  local -a projs=()
  mapfile -t projs <<<"$plan_out"
  if (( ${#projs[@]} > 6 )); then
    echo "==== dotnet test (${#projs[@]} affected — full solution, $DOTNET_FILTER) ===="
    local rc=0
    ( cd "$ROOT/app" && dotnet test Laplace.slnx -c Release --nologo --verbosity minimal \
        --filter "$DOTNET_FILTER" ) || rc=$?
    if [[ "$rc" -eq 0 ]]; then
      "$PYTHON" "$ROOT/scripts/affected-app.py" record --ns test --salt "$salt"
    fi
    return "$rc"
  fi

  echo "==== dotnet test (${#projs[@]} affected project(s), $DOTNET_FILTER) ===="
  local p name rc=0
  local -a passed=()
  for p in "${projs[@]}"; do
    echo "---- dotnet test $p ----"
    if ( cd "$ROOT/app" && dotnet test "$p" -c Release --nologo --verbosity minimal \
           --filter "$DOTNET_FILTER" ); then
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

parallel_pair() {
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
  engine)  run_ctest_engine; exit $? ;;
  regress) run_ctest_regress; exit $? ;;
  app)     run_dotnet; exit $? ;;
  integration)
    if [[ "$SERIAL" == "1" ]]; then
      run_ctest_regress
      run_dotnet
    else
      echo "==== parallel: ctest-regress || dotnet ===="
      parallel_pair ctest-regress run_ctest_regress dotnet run_dotnet
    fi
    echo "==== test-parallel (integration) OK ===="
    exit 0 ;;
esac

if [[ "$SERIAL" == "1" ]]; then
  run_ctest_engine
  run_ctest_regress
  run_dotnet
  exit 0
fi

echo "==== parallel: ctest-engine || ctest-regress ===="
parallel_pair ctest-engine run_ctest_engine ctest-regress run_ctest_regress
run_dotnet
echo "==== test-parallel OK ===="
