#!/usr/bin/env bash
# Shared environment owner for composite actions and the reserved lifecycle.
set -euo pipefail
laplace_ci_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
set +u
source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1
set -u
export LD_LIBRARY_PATH="$laplace_ci_root/build/engine/core:$laplace_ci_root/build/engine/dynamics:$laplace_ci_root/build/engine/synthesis:${LD_LIBRARY_PATH:-}"
export PGDATABASE="${PGDATABASE:-laplace}"
export LAPLACE_QUERY_DB="${LAPLACE_QUERY_DB:-laplace}"
export LAPLACE_DB="${LAPLACE_DB:-Host=/var/run/postgresql;Username=laplace_admin;Database=${PGDATABASE}}"
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
export DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDUSESERVER=0
laplace_ci_cpus="$(nproc 2>/dev/null || echo 1)"
export CMAKE_BUILD_PARALLEL_LEVEL="${CMAKE_BUILD_PARALLEL_LEVEL:-$laplace_ci_cpus}"
if [[ -z "${CTEST_PARALLEL_LEVEL:-}" ]]; then
  if [[ "${LAPLACE_TEST_SERIAL:-}" == 1 ]]; then
    export CTEST_PARALLEL_LEVEL=1
  else
    export CTEST_PARALLEL_LEVEL="$laplace_ci_cpus"
  fi
fi
if [[ "${LAPLACE_SETUP_REQUIRE_BUILT_REVISION:-false}" == true ]]; then
  laplace_ci_expected="$(git -C "$laplace_ci_root" rev-parse HEAD)"
  laplace_ci_actual="$(cat "$laplace_ci_root/build/.laplace-source-revision" 2>/dev/null || true)"
  [[ "$laplace_ci_actual" == "$laplace_ci_expected" ]] || {
    echo "::error::prepared runtime does not belong to this checkout revision" >&2
    return 1
  }
fi
if [[ "${LAPLACE_SETUP_USE_CMAKE:-true}" == true ]]; then
  laplace_ci_cmake_args=(
    --root "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/tools/cmake"
    --work "${LAPLACE_WORK_ROOT:-/build/laplace/work}/cmake")
  if [[ "${LAPLACE_PROVISION_TOOLS:-0}" == 1 ]]; then
    laplace_ci_cmake_args+=(--ensure)
  fi
  laplace_ci_cmake_bin="$(python3 "$laplace_ci_root/scripts/provision-cmake.py" "${laplace_ci_cmake_args[@]}")"
  export PATH="$laplace_ci_cmake_bin:$PATH"
fi
# Composite actions need the same selected values in later steps. A single
# reserved shell already inherits these exports directly.
if [[ -n "${GITHUB_ENV:-}" ]]; then
  for laplace_ci_name in ONEAPI_ROOT CMPLR_ROOT MKLROOT TBBROOT CPATH LIBRARY_PATH PKG_CONFIG_PATH SETVARS_COMPLETED LD_LIBRARY_PATH PGDATABASE LAPLACE_QUERY_DB LAPLACE_DB MSBUILDDISABLENODEREUSE UseSharedCompilation DOTNET_CLI_USE_MSBUILD_SERVER MSBUILDUSESERVER CMAKE_BUILD_PARALLEL_LEVEL CTEST_PARALLEL_LEVEL PATH; do
    if [[ -v "$laplace_ci_name" ]]; then
      printf '%s=%s\n' "$laplace_ci_name" "${!laplace_ci_name}" >> "$GITHUB_ENV"
    fi
  done
fi

# CI runs never attach a debugger or tracer; without this every dotnet process leaves
# clr-debug-pipe-* / dotnet-diagnostic-* IPC files in its TMPDIR (the work root).
export DOTNET_EnableDiagnostics=0
