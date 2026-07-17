#!/usr/bin/env bash
# Build system deps (proj/geos/gdal/postgresql/postgis/tree-sitter) into /opt/laplace.
#
# Idempotent: fingerprints /opt/laplace/external pins + CMakeLists + ISA. If the
# fingerprint matches and install artifacts exist, this is a no-op. Rebuilds only
# when sources/pins actually change (or LAPLACE_FORCE_DEPS=1).
#
# Used by: scripts/setup-host.sh Layer 0.5, CI deps job, `just build-deps`.
#
# Env:
#   LAPLACE_EXTERNAL       default /opt/laplace/external
#   LAPLACE_DEPS_BUILD     default /opt/laplace/build/deps
#   LAPLACE_DEPS_PREFIX    default /opt/laplace
#   LAPLACE_TARGET_ISA     default AVX2
#   LAPLACE_FORCE_DEPS=1   ignore stamp; rebuild
#   LAPLACE_DEPS_USER      default laplace-runner (build as this user when root)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXT="${LAPLACE_EXTERNAL:-/opt/laplace/external}"
BUILD="${LAPLACE_DEPS_BUILD:-/opt/laplace/build/deps}"
PREFIX="${LAPLACE_DEPS_PREFIX:-/opt/laplace}"
ISA="${LAPLACE_TARGET_ISA:-AVX2}"
RUN_AS="${LAPLACE_DEPS_USER:-laplace-runner}"
STAMP_DIR="$BUILD"
STAMP_FILE="$STAMP_DIR/.laplace-deps.fingerprint"
FORCE="${LAPLACE_FORCE_DEPS:-0}"

DEPS=(proj geos gdal postgresql postgis tree-sitter)

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }

deps_fingerprint() {
  local d rev
  {
    echo "isa=$ISA"
    echo "prefix=$PREFIX"
    echo "external=$EXT"
    if [ -f "$ROOT/external/CMakeLists.txt" ]; then
      # Prefer sha256sum; fall back to cksum
      if command -v sha256sum >/dev/null 2>&1; then
        echo "cmake=$(sha256sum "$ROOT/external/CMakeLists.txt" | awk '{print $1}')"
      else
        echo "cmake=$(cksum "$ROOT/external/CMakeLists.txt" | awk '{print $1"-"$2}')"
      fi
    else
      echo "cmake=MISSING"
    fi
    for d in "${DEPS[@]}"; do
      if [ -d "$EXT/$d/.git" ]; then
        rev="$(git -C "$EXT/$d" rev-parse HEAD 2>/dev/null || echo UNKNOWN)"
        echo "$d=$rev"
      elif [ -d "$EXT/$d" ]; then
        echo "$d=NOGIT"
      else
        echo "$d=MISSING"
      fi
    done
  } | if command -v sha256sum >/dev/null 2>&1; then
        sha256sum | awk '{print $1}'
      else
        cksum | awk '{print $1"-"$2}'
      fi
}

installs_present() {
  [ -x "$PREFIX/pgsql-18/bin/postgres" ] || return 1
  [ -e "$PREFIX/proj/lib" ] || [ -e "$PREFIX/proj/lib64" ] || return 1
  [ -e "$PREFIX/geos/lib" ] || [ -e "$PREFIX/geos/lib64" ] || return 1
  [ -e "$PREFIX/gdal/lib" ] || [ -e "$PREFIX/gdal/lib64" ] || return 1
  # CI's "Verify installed dep artifacts" (laplace.yml deps job) also requires
  # these two — a stamp-skip here with either missing fails four steps later.
  [ -e "$PREFIX/pgsql-18/lib/postgis-3.so" ] || return 1
  [ -e "$PREFIX/tree-sitter/lib/libtree-sitter.a" ] || return 1
  return 0
}

run_as_builder() {
  local cmd="$*"
  if [ "$(id -u)" -eq 0 ] && id -u "$RUN_AS" >/dev/null 2>&1; then
    # laplace-runner is nologin. `script -c` runs via $SHELL/pw_shell — without
    # SHELL=/bin/bash that prints "This account is currently not available."
    # PTY still wanted so ExternalProject/make stream under sudo -u.
    if command -v script >/dev/null 2>&1; then
      sudo -u "$RUN_AS" -H env SHELL=/bin/bash PATH="$PATH" \
        script -qefc "$cmd" /dev/null
    else
      sudo -u "$RUN_AS" -H PATH="$PATH" stdbuf -oL -eL /bin/bash -c "$cmd"
    fi
  else
    /bin/bash -c "$cmd"
  fi
}

invalidate_ep_stamps() {
  # Drop ExternalProject step stamps so a pin change re-enters configure/build/install.
  # Keep object trees where possible — cmake/make still incremental inside BINARY_DIR.
  find "$BUILD" -type d -name '*-stamp' 2>/dev/null | while read -r d; do
    rm -rf "$d"
  done
}

# /opt/laplace/build/deps is shared across checkouts (setup-host under ~/Projects,
# CI under actions-runner/_work/...). CMake refuses -S when CMAKE_HOME_DIRECTORY
# in the cache differs — scrub top-level cache only; EP binary dirs stay.
scrub_cmake_cache_if_source_moved() {
  local cache="$BUILD/CMakeCache.txt"
  local want src
  [ -f "$cache" ] || return 0
  want="$(cd "$ROOT/external" && pwd -P)"
  src="$(grep -E '^CMAKE_HOME_DIRECTORY:' "$cache" | head -n1 | cut -d= -f2- || true)"
  [ -n "$src" ] || return 0
  if [ -d "$src" ]; then
    src="$(cd "$src" && pwd -P)"
  fi
  if [ "$src" != "$want" ]; then
    yellow "cmake source moved: cache=$src want=$want — scrubbing CMakeCache (+ CMakeFiles)"
    rm -f "$cache"
    rm -rf "$BUILD/CMakeFiles"
  fi
}

# --- main ---
if [ ! -d "$EXT" ]; then
  red "missing $EXT — run sync-external / setup-host prefix first"
  exit 1
fi
if [ ! -f "$ROOT/external/CMakeLists.txt" ]; then
  red "missing $ROOT/external/CMakeLists.txt"
  exit 1
fi
if ! grep -q 'USES_TERMINAL_BUILD' "$ROOT/external/CMakeLists.txt"; then
  red "$ROOT/external/CMakeLists.txt missing USES_TERMINAL_BUILD"
  exit 1
fi

if [ "$(id -u)" -eq 0 ]; then
  install -d -m 2775 -o "$RUN_AS" -g "$RUN_AS" "$(dirname "$BUILD")" "$BUILD" 2>/dev/null \
    || mkdir -p "$BUILD"
else
  mkdir -p "$BUILD"
fi

fp="$(deps_fingerprint)"
echo "deps fingerprint: $fp"

write_stamp() {
  printf '%s\n' "$fp" >"$STAMP_FILE"
  if [ "$(id -u)" -eq 0 ] && id -u "$RUN_AS" >/dev/null 2>&1; then
    chown "$RUN_AS:$RUN_AS" "$STAMP_FILE" 2>/dev/null || true
  fi
}

if [ "$FORCE" != "1" ] && installs_present; then
  if [ -f "$STAMP_FILE" ] && [ "$(cat "$STAMP_FILE")" = "$fp" ]; then
    green "✓ system deps current (fingerprint match) — skip build"
    echo "  stamp: $STAMP_FILE"
    echo "  force: LAPLACE_FORCE_DEPS=1 $0"
    exit 0
  fi
  if [ ! -f "$STAMP_FILE" ]; then
    # First run after stamp introduction: trust existing /opt/laplace installs.
    yellow "no stamp yet; installs present — adopting fingerprint (skip build)"
    yellow "  if installs are stale vs pins: LAPLACE_FORCE_DEPS=1 $0"
    write_stamp
    green "✓ stamped $STAMP_FILE"
    exit 0
  fi
fi

if [ "$FORCE" = "1" ]; then
  yellow "LAPLACE_FORCE_DEPS=1 — rebuilding"
  invalidate_ep_stamps
elif [ -f "$STAMP_FILE" ] && [ "$(cat "$STAMP_FILE" 2>/dev/null)" != "$fp" ]; then
  yellow "deps sources/pins changed — invalidating ExternalProject stamps"
  invalidate_ep_stamps
elif ! installs_present; then
  yellow "install artifacts missing under $PREFIX — building"
fi

scrub_cmake_cache_if_source_moved

echo "==== cmake configure $BUILD (LAPLACE_EXTERNAL=$EXT) ===="
# Fail loud: do not swallow cmake configure/build errors (set -e already; keep explicit).
if ! run_as_builder "cmake -B '$BUILD' -S '$ROOT/external' -ULAPLACE_EXTERNAL -DLAPLACE_EXTERNAL='$EXT' -DLAPLACE_DEPS_PREFIX='$PREFIX'"; then
  red "cmake configure failed for $BUILD (source=$ROOT/external)"
  exit 1
fi

echo "==== cmake --build $BUILD -j ===="
if ! run_as_builder "cmake --build '$BUILD' -j"; then
  red "cmake --build failed for $BUILD"
  exit 1
fi

if ! installs_present; then
  red "build finished but install artifacts still missing under $PREFIX"
  exit 1
fi

write_stamp
green "✓ system deps built + stamped ($STAMP_FILE)"

