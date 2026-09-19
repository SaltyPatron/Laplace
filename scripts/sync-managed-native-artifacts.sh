#!/usr/bin/env bash
# Make every already-built managed output execute the native artifact from THIS tree.
#
# Directory.Build.props copies native libraries beside managed binaries at build time, but
# native-only source changes do not necessarily invalidate affected-app's C# project graph.
# On the persistent runner that leaves an older app-local .so in bin/Release. .NET probes
# the app directory before the OS loader/LD_LIBRARY_PATH, so managed parity tests can then
# execute stale native code even though build/engine/core contains the new library.
#
# This is a synchronization step, not a build: only existing managed output roots are
# touched, and bytes always come from the exact native build tree the test profile is about
# to validate.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if [[ "${LAPLACE_REUSE_INSTALLED_NATIVE:-0}" == 1 ]]; then
  native_root="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/lib"
  native=(
    "$native_root/liblaplace_core.so"
    "$native_root/liblaplace_dynamics.so"
    "$native_root/liblaplace_synthesis.so"
    "$native_root/liblaplace_syzygy.so"
  )
  native_label="installed"
else
  native=(
    "build/engine/core/liblaplace_core.so"
    "build/engine/dynamics/liblaplace_dynamics.so"
    "build/engine/synthesis/liblaplace_synthesis.so"
    "build/engine/core/liblaplace_syzygy.so"
  )
  native_label="candidate"
fi
core_source="${native[0]}"
if [[ ! -f "$core_source" ]]; then
  echo "::error::$native_label native core missing — build native or install the selected closure before managed tests" >&2
  exit 1
fi

mapfile -t outputs < <(
  for dir in "$ROOT"/app/*/bin/Release/*; do
    [[ -d "$dir" ]] && printf '%s\n' "$dir"
  done
  if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
    for dir in "$LAPLACE_BUILD_ROOT"/app/bin/*/Release/*; do
      [[ -d "$dir" ]] && printf '%s\n' "$dir"
    done
  fi
)

if (( ${#outputs[@]} == 0 )); then
  echo "::error::no managed Release outputs exist — run pipeline.sh build before managed tests" >&2
  exit 1
fi

copied=0
for dir in "${outputs[@]}"; do
  for src in "${native[@]}"; do
    [[ -f "$src" ]] || continue
    install -m 775 "$src" "$dir/$(basename "$src")"
    copied=$((copied + 1))
  done
done

# The strongest contract is the same one NativeArtifactIdentityTests asserts: at least the
# core test host's app-local image must be byte-identical to build/engine/core.
core_test="$ROOT/app/Laplace.Core.Tests/bin/Release/net10.0/liblaplace_core.so"
if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
  core_test="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Core.Tests/Release/net10.0/liblaplace_core.so"
fi
if [[ -d "$(dirname "$core_test")" ]]; then
  [[ -f "$core_test" ]] || {
    echo "::error::managed core test output has no app-local liblaplace_core.so after sync" >&2
    exit 1
  }
  cmp -s "$core_source" "$core_test" || {
    echo "::error::managed core test native image still differs from exact $native_label source" >&2
    exit 1
  }
fi

echo "managed native artifact sync: source=$native_label ${#outputs[@]} output roots, $copied copies"
