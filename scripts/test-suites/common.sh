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
  # currently installed floor. The physical CMake tree lives on /build and the
  # checkout-local build symlink is not an authority for artifact selection.
  build_dir="$(python3 "$ROOT/scripts/place-build-directory.py" "$ROOT")"
  candidate=$(find -L "$build_dir" -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  [[ -n "$candidate" ]] || {
    echo "::error::candidate T0 perfcache missing under $build_dir — build native before managed qualification" >&2
    return 1
  }
  export LAPLACE_PERFCACHE_BIN="$candidate"
  export LAPLACE_ENGINE_BUILD="$build_dir/engine"
  echo "::notice::managed dev qualification T0 ROM: $LAPLACE_PERFCACHE_BIN"
}

set_installed_perfcache() {
  local candidate
  candidate=$(find "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/share/laplace" -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  if [[ -z "$candidate" ]]; then
    candidate=$(find -L build -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  fi
  [[ -z "$candidate" ]] || export LAPLACE_PERFCACHE_BIN="$candidate"
}

run_ctest() {
  python3 "$ROOT/scripts/provision-cmake.py" \
    --root "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/tools/cmake" \
    --work "${LAPLACE_WORK_ROOT:-/build/laplace/work}/cmake" \
    --exec-tool ctest -- "$@"
}

managed_test_solution() {
  local selected="$1" label="$2" work solution
  if [[ "$selected" == all ]]; then
    printf '%s\n' "$ROOT/app/Laplace.slnx"
    return 0
  fi
  [[ -n "$selected" ]] || return 3
  work="${LAPLACE_WORK_ROOT:-/build/laplace/work}/managed-solutions"
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
  local impact_filter="${LAPLACE_MANAGED_TEST_FILTER:-}"
  if [[ "$label" == managed-dev && -n "$impact_filter" ]]; then
    filter="($filter)&($impact_filter)"
  fi
  solution="$(managed_test_solution "$selected" "$label")" || return $?
  [[ "$solution" == "$ROOT/app/Laplace.slnx" ]] || generated="$solution"

  local test_log_dir test_log
  test_log_dir="${LAPLACE_WORK_ROOT:-/build/laplace/work}/managed-test-logs"
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
