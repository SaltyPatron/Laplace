#!/usr/bin/env bash
# Make every already-built managed output execute the native artifact from THIS tree.
#
# Directory.Build.targets synchronizes native libraries beside managed binaries, but
# native-only source changes do not necessarily invalidate affected-app's C# project graph.
# On the persistent runner that leaves an older app-local .so in bin/Release. .NET probes
# the app directory before the OS loader/LD_LIBRARY_PATH, so managed parity tests can then
# execute stale native code even though build/engine/core contains the new library.
#
# --output selects one build or publish directory. Without it, synchronize the
# existing Release outputs used by the managed test profile.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

outputs=()
while (($#)); do
  case $1 in
    --output)
      [[ $# -ge 2 && -n $2 ]] || { echo 'Usage: sync-managed-native-artifacts.sh [--output DIRECTORY]...' >&2; exit 2; }
      outputs+=("$2"); shift 2 ;;
    *) echo "Unknown native synchronization argument: $1" >&2; exit 2 ;;
  esac
done
explicit_outputs=${#outputs[@]}

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
  engine_build="${LAPLACE_ENGINE_BUILD:-$ROOT/build/engine}"
  native=(
    "$engine_build/core/liblaplace_core.so"
    "$engine_build/dynamics/liblaplace_dynamics.so"
    "$engine_build/synthesis/liblaplace_synthesis.so"
    "$engine_build/core/liblaplace_syzygy.so"
  )
  native_label="candidate"
fi
core_source="${native[0]}"
if [[ ! -f "$core_source" ]]; then
  echo "::error::$native_label native core missing — build native or install the selected closure before managed tests" >&2
  exit 1
fi

if (( explicit_outputs == 0 )); then
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
fi

if (( ${#outputs[@]} == 0 )); then
  echo "::error::no managed Release outputs exist — run pipeline.sh build before managed tests" >&2
  exit 1
fi

# Keep one physical library per source version. Publish the real files before
# their aliases, and replace changed files by rename so mapped images retain
# their old inode. Content comparison also catches restored or older mtimes.
shopt -s nullglob
artifacts=()
for src in "${native[@]}"; do
  artifacts+=("$src" "$src".[0-9]*)
done
copied=0
linked=0
unchanged=0
pending=
trap '[[ -z "$pending" ]] || rm -f -- "$pending"' EXIT
for dir in "${outputs[@]}"; do
  mkdir -p -- "$dir"
  for src in "${artifacts[@]}"; do
    [[ -f "$src" && ! -L "$src" ]] || continue
    destination="$dir/$(basename -- "$src")"
    if [[ -f "$destination" && ! -L "$destination" ]] && cmp -s -- "$src" "$destination"; then
      unchanged=$((unchanged + 1))
      continue
    fi
    pending=$(mktemp "$dir/.laplace-native.XXXXXX")
    cp --preserve=mode,timestamps -- "$src" "$pending"
    mv -fT -- "$pending" "$destination"
    pending=
    copied=$((copied + 1))
  done
  for src in "${artifacts[@]}"; do
    [[ -L "$src" ]] || continue
    alias=$(basename -- "$(readlink -- "$src")")
    [[ "$(dirname -- "$src")/$alias" -ef "$src" ]] || {
      echo "::error::native alias is not contained in its library directory: $src" >&2
      exit 1
    }
    destination="$dir/$(basename -- "$src")"
    if [[ -L "$destination" && $(readlink -- "$destination") == "$alias" ]]; then
      unchanged=$((unchanged + 1))
      continue
    fi
    pending=$(mktemp "$dir/.laplace-native.XXXXXX")
    ln -sfn -- "$alias" "$pending"
    mv -fT -- "$pending" "$destination"
    pending=
    linked=$((linked + 1))
  done
  cmp -s -- "$core_source" "$dir/$(basename -- "$core_source")" || {
    echo "::error::managed native core differs from exact $native_label source: $dir" >&2
    exit 1
  }
done

echo "managed native artifact sync: source=$native_label ${#outputs[@]} output roots, $copied copies, $linked links, $unchanged unchanged"
