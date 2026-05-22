#!/bin/bash
# scripts/build-proj.sh
#
# Build PROJ (coordinate-transformation library) from external/proj/
# (submodule pinned to 9.4.1 per Story B.4 / commit 574216e) with the
# Intel toolchain (icx/icpx, per ADR 0028) and install to /opt/laplace/proj/.
#
# Idempotent. Usage:
#   scripts/build-proj.sh              # build + install (idempotent)
#   scripts/build-proj.sh --clean      # wipe build dir, rebuild from scratch
#
# Prerequisites (handled by bootstrap_build_environment):
#   - libsqlite3-dev, libtiff-dev, libcurl4-openssl-dev (PROJ's runtime deps)
#   - cmake, ninja-build
#   - /opt/laplace/ owned by laplace-runner
#
# Per Story (slot in Epic B between B.6 and B.7).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SRC="$REPO_DIR/external/proj"
BUILD="$SRC/build"
PREFIX="${LAPLACE_PROJ_PREFIX:-/opt/laplace/proj}"
TARGET_ISA="${LAPLACE_TARGET_ISA:-AVX2}"

DO_CLEAN=0
for arg in "$@"; do
    case "$arg" in
        --clean) DO_CLEAN=1 ;;
        -h|--help) sed -n '2,/^$/p' "$0"; exit 0 ;;
        *) echo "Unknown flag: $arg" >&2; exit 64 ;;
    esac
done

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "=== $1 ==="; }

say "Preflight"
[ -d "$SRC" ] || { red "Missing $SRC — submodule init'd? Run: git submodule update --init external/proj"; exit 1; }

if [ -f /opt/intel/oneapi/setvars.sh ]; then
    # shellcheck disable=SC1091
    source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1 || true
fi
command -v icx >/dev/null  || { red "icx not in PATH"; exit 1; }
command -v icpx >/dev/null || { red "icpx not in PATH"; exit 1; }
command -v cmake >/dev/null || { red "cmake not installed"; exit 1; }

case "$TARGET_ISA" in
    AVX2)    MARCH_FLAG="-march=haswell" ;;
    AVX512)  MARCH_FLAG="-march=sapphirerapids" ;;
    native)  MARCH_FLAG="-march=native" ;;
    *)       red "Unknown LAPLACE_TARGET_ISA=$TARGET_ISA"; exit 1 ;;
esac

green "✓ PROJ src: $SRC ($(cd "$SRC" && git rev-parse --short HEAD))"
green "✓ Prefix:   $PREFIX"
green "✓ Target:   $TARGET_ISA ($MARCH_FLAG)"

if [ "$DO_CLEAN" = 1 ]; then
    say "Clean"
    rm -rf "$BUILD"
    green "✓ $BUILD removed"
fi

say "Configure (CMake)"
mkdir -p "$BUILD"
cmake -S "$SRC" -B "$BUILD" \
    -G "Unix Makefiles" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DCMAKE_C_COMPILER=icx \
    -DCMAKE_CXX_COMPILER=icpx \
    -DCMAKE_C_FLAGS="-O3 $MARCH_FLAG -fno-fast-math -ffp-contract=off" \
    -DCMAKE_CXX_FLAGS="-O3 $MARCH_FLAG -fno-fast-math -ffp-contract=off" \
    -DBUILD_TESTING=OFF \
    -DBUILD_SHARED_LIBS=ON \
    -DENABLE_CURL=ON \
    -DENABLE_TIFF=ON \
    >/dev/null
green "✓ Configured"

say "Build (make -j$(nproc))"
cmake --build "$BUILD" -j"$(nproc)"
green "✓ Built"

say "Install to $PREFIX"
cmake --install "$BUILD"
green "✓ Installed"

say "Verification"
"$PREFIX/bin/proj" 2>&1 | head -1 || true
ls "$PREFIX/lib/libproj.so"* 2>&1 | head -3
green "===== build-proj.sh DONE ====="
