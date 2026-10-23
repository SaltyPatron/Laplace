#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init

if [[ "${LAPLACE_TEST_SERIAL:-0}" == 1 ]]; then
  export CTEST_PARALLEL_LEVEL=1
elif [[ -z "${CTEST_PARALLEL_LEVEL:-}" ]]; then
  CTEST_PARALLEL_LEVEL="$(nproc 2>/dev/null || echo 1)"
  export CTEST_PARALLEL_LEVEL
fi

sync_managed_native() {
  bash scripts/sync-managed-native-artifacts.sh
}

set_dev_perfcache() {
  local build_dir candidate
  if [[ -n "${LAPLACE_CANDIDATE_ENGINE_BUILD:-}" ]]; then
    build_dir="$LAPLACE_CANDIDATE_ENGINE_BUILD"
    [[ -f "$build_dir/CMakeCache.txt" ]] || { echo "::error::candidate CMake build missing: $build_dir" >&2; return 1; }
    set_native_caches "$build_dir/core/perfcache"
    export LAPLACE_ENGINE_BUILD="$build_dir"
    echo "::notice::managed dev qualification candidate ROM pair: $LAPLACE_TIER0, $LAPLACE_HIGHWAY"
    return 0
  fi
  if [[ "${LAPLACE_REUSE_INSTALLED_NATIVE:-0}" == 1 ]]; then
    set_installed_perfcache
    [[ -n "${LAPLACE_PERFCACHE_BIN:-}" && -f "$LAPLACE_PERFCACHE_BIN" ]] || {
      echo "::error::installed T0 perfcache missing — managed-only qualification requires the installed native closure" >&2
      return 1
    }
    echo "::notice::managed dev qualification installed T0 ROM: $LAPLACE_PERFCACHE_BIN"
    return 0
  fi
  # Pre-install qualification must execute the exact candidate ROM, never the
  # currently installed floor. Resolve the CMake tree from machine configuration.
  build_dir="$(python3 "$ROOT/scripts/place-build-directory.py" "$ROOT")"
  candidate="$build_dir/engine/core/perfcache/tier0.bin"
  [[ -f "$candidate" ]] || candidate="$build_dir/core/perfcache/tier0.bin"
  [[ -f "$candidate" ]] || {
    echo "::error::candidate T0 perfcache missing under $build_dir — build native before managed qualification" >&2
    return 1
  }
  set_native_caches "$(dirname "$candidate")"
  export LAPLACE_ENGINE_BUILD="${candidate%/core/perfcache/tier0.bin}"
  echo "::notice::managed dev qualification T0 ROM: $LAPLACE_PERFCACHE_BIN"
}

set_installed_perfcache() {
  set_native_caches "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/share/laplace"
}

set_native_caches() {
  local directory="$1"
  [[ -f "$directory/tier0.bin" && -f "$directory/highway.bin" ]] || {
    echo "::error::canonical Native tier0.bin/highway.bin pair missing in $directory" >&2
    return 1
  }
  export LAPLACE_TIER0="$directory/tier0.bin"
  export LAPLACE_PERFCACHE_BIN="$LAPLACE_TIER0"
  export LAPLACE_HIGHWAY="$directory/highway.bin"
}

run_ctest() {
  python3 "$ROOT/scripts/provision-cmake.py" \
    --root "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/tools/cmake" \
    --work "${LAPLACE_WORK_ROOT:?}/cmake" \
    --exec-tool ctest -- "$@"
}

managed_test_solution() {
  local selected="$1" label="$2" work solution
  if [[ "$selected" == all ]]; then
    printf '%s\n' "$ROOT/app/Laplace.slnx"
    return 0
  fi
  [[ -n "$selected" ]] || return 3
  work="${LAPLACE_WORK_ROOT:?}/managed-solutions"
  mkdir -p "$work"
  solution="$(mktemp "$work/${label}.XXXXXX.slnx")"
  python3 "$ROOT/scripts/ci_managed_projects.py" --root "$ROOT" solution \
    --projects "$selected" --output "$solution"
  printf '%s\n' "$solution"
}

run_managed_dotnet_tests() {
  local selected="$1" label="$2" filter="$3"
  shift 3
  if [[ -z "$selected" ]]; then
    echo "::notice::managed impact plan selected no $label test projects"
    return 0
  fi

  local solution generated="" rc=0 deadline="${LAPLACE_MANAGED_TEST_TIMEOUT:-15m}"
  local impact_filter=""
  case "$label" in
    managed-dev) impact_filter="${LAPLACE_MANAGED_TEST_FILTER:-}" ;;
    managed-db) impact_filter="${LAPLACE_MANAGED_DB_TEST_FILTER:-}" ;;
    managed-live) impact_filter="${LAPLACE_MANAGED_LIVE_TEST_FILTER:-}" ;;
  esac
  if [[ -n "$impact_filter" ]]; then
    filter="($filter)&($impact_filter)"
  fi
  solution="$(managed_test_solution "$selected" "$label")" || return $?
  [[ "$solution" == "$ROOT/app/Laplace.slnx" ]] || generated="$solution"

  local test_log_dir test_log
  test_log_dir="${LAPLACE_WORK_ROOT:?}/managed-test-logs"
  mkdir -p "$test_log_dir"
  test_log="$(mktemp "$test_log_dir/${label}.XXXXXX.log")"

  echo "::notice::$label projects=$selected deadline=$deadline filter=$filter"
  timeout --signal=TERM --kill-after=30s "$deadline" \
    dotnet test "$solution" -c Release --no-build --nologo --verbosity minimal \
      "$@" --filter "$filter" 2>&1 | tee "$test_log" || rc=$?

  # A solution run reports "No test matches" once for every project without
  # the selected trait.  Reject only when the complete solution produced no
  # positive test total; otherwise those per-project notices are expected.
  if (( rc == 0 )) && grep -Fq "No test matches" "$test_log" \
      && ! grep -Eq 'Total:[[:space:]]*[1-9][0-9]*' "$test_log"; then
    echo "::error::$label filter matched zero tests: $filter" >&2
    rc=4
  fi

  [[ -z "$generated" ]] || rm -f "$generated"
  rm -f "$test_log"
  if (( rc == 124 || rc == 137 )); then
    echo "::error::$label exceeded managed test deadline $deadline" >&2
  fi
  return "$rc"
}
