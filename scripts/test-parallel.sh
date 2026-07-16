#!/usr/bin/env bash
# Parallel test orchestration for Linux (parity with scripts/win/test-all.cmd).
#
# Usage:
#   scripts/test-parallel.sh              # ctest (excl. regress) || regress || then dotnet
#   scripts/test-parallel.sh --engine     # ctest -LE regress only
#   scripts/test-parallel.sh --regress    # ctest -L regress only
#   scripts/test-parallel.sh --app        # dotnet test only
#   scripts/test-parallel.sh --serial     # force serial (or set LAPLACE_TEST_SERIAL=1)
#
# Env:
#   CTEST_PARALLEL_LEVEL / CMAKE_BUILD_PARALLEL_LEVEL — from nproc if unset
#   LAPLACE_TEST_SERIAL=1 — serial ctest + serial layers

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

MODE=all
SERIAL="${LAPLACE_TEST_SERIAL:-0}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --engine)  MODE=engine; shift ;;
    --regress) MODE=regress; shift ;;
    --app)     MODE=app; shift ;;
    --serial)  SERIAL=1; shift ;;
    -h|--help)
      sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
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

run_ctest_engine() {
  echo "==== ctest (excl. regress) -j ${CTEST_PARALLEL_LEVEL} ===="
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -LE regress
}

run_ctest_regress() {
  echo "==== ctest (regress) -j ${CTEST_PARALLEL_LEVEL} ===="
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -L regress
}

run_dotnet() {
  echo "==== dotnet test (Tier!=perf) ===="
  local bin=""
  bin=$(find "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/share/laplace" "$ROOT/build" \
    -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1 || true)
  if [[ -n "$bin" ]]; then
    export LAPLACE_PERFCACHE_BIN="$bin"
    echo "LAPLACE_PERFCACHE_BIN=$bin"
  fi
  ( cd "$ROOT/app" && dotnet test Laplace.slnx -c Release --nologo --verbosity minimal \
      --filter 'Tier!=perf & Tier!=db' )
}

case "$MODE" in
  engine)  run_ctest_engine; exit $? ;;
  regress) run_ctest_regress; exit $? ;;
  app)     run_dotnet; exit $? ;;
esac

# Full gate: engine ∥ regress, then app (unless --serial).
if [[ "$SERIAL" == "1" ]]; then
  run_ctest_engine
  run_ctest_regress
  run_dotnet
  exit 0
fi

echo "==== parallel: ctest-engine || ctest-regress ===="
eng_log="$(mktemp)"
reg_log="$(mktemp)"
set +e
run_ctest_engine >"$eng_log" 2>&1 &
eng_pid=$!
run_ctest_regress >"$reg_log" 2>&1 &
reg_pid=$!
wait "$eng_pid"; eng_rc=$?
wait "$reg_pid"; reg_rc=$?
set -e
echo "---- ctest-engine ----"; cat "$eng_log"
echo "---- ctest-regress ----"; cat "$reg_log"
rm -f "$eng_log" "$reg_log"
if [[ "$eng_rc" -ne 0 || "$reg_rc" -ne 0 ]]; then
  echo "::error::native test layer failed (engine_rc=$eng_rc regress_rc=$reg_rc)"
  exit 1
fi

run_dotnet
echo "==== test-parallel OK ===="
