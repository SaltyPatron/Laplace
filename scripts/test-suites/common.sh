#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

# shellcheck source=scripts/lib/storage.sh
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
  solution="$(managed_test_solution "$selected" "$label")" || return $?
  [[ "$solution" == "$ROOT/app/Laplace.slnx" ]] || generated="$solution"

  echo "::notice::$label projects=$selected deadline=$deadline"
  timeout --signal=TERM --kill-after=30s "$deadline" \
    dotnet test "$solution" -c Release --no-build --nologo --verbosity minimal \
      "$@" --filter "$filter" || rc=$?

  [[ -z "$generated" ]] || rm -f "$generated"
  if (( rc == 124 || rc == 137 )); then
    echo "::error::$label exceeded managed test deadline $deadline" >&2
  fi
  return "$rc"
}
