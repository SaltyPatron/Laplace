#!/bin/bash
# scripts/build-gdal.sh
#
# Build GDAL (geospatial data abstraction library) from external/gdal/
# (submodule pinned to v3.9.3 per commit 574216e) with the Intel
# toolchain. Installs to /opt/laplace/gdal/.
#
# Depends on PROJ being built first (scripts/build-proj.sh). GDAL's
# configure auto-discovers PROJ via pkg-config if /opt/laplace/proj/
# is on PKG_CONFIG_PATH (this script sets that).
#
# Idempotent. Usage:
#   scripts/build-gdal.sh              # build + install
#   scripts/build-gdal.sh --clean
#
# Prerequisites: cmake, build-deps (per bootstrap_build_environment),
# PROJ built to /opt/laplace/proj/.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SRC="$REPO_DIR/external/gdal"
BUILD="$SRC/build"
PREFIX="${LAPLACE_GDAL_PREFIX:-/opt/laplace/gdal}"
PROJ_PREFIX="${LAPLACE_PROJ_PREFIX:-/opt/laplace/proj}"
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
[ -d "$SRC" ] || { red "Missing $SRC — submodule init'd?"; exit 1; }
[ -x "$PROJ_PREFIX/bin/proj" ] || { red "PROJ not found at $PROJ_PREFIX — run scripts/build-proj.sh first"; exit 1; }

if [ -f /opt/intel/oneapi/setvars.sh ]; then
    # shellcheck disable=SC1091
    source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1 || true
fi
command -v icx >/dev/null  || { red "icx not in PATH"; exit 1; }
command -v icpx >/dev/null || { red "icpx not in PATH"; exit 1; }

# Tell GDAL's configure how to find PROJ
export PKG_CONFIG_PATH="$PROJ_PREFIX/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
export LD_LIBRARY_PATH="$PROJ_PREFIX/lib:${LD_LIBRARY_PATH:-}"

case "$TARGET_ISA" in
    AVX2)    MARCH_FLAG="-march=haswell" ;;
    AVX512)  MARCH_FLAG="-march=sapphirerapids" ;;
    native)  MARCH_FLAG="-march=native" ;;
    *)       red "Unknown LAPLACE_TARGET_ISA=$TARGET_ISA"; exit 1 ;;
esac

green "✓ GDAL src:  $SRC ($(cd "$SRC" && git rev-parse --short HEAD))"
green "✓ Prefix:    $PREFIX"
green "✓ PROJ from: $PROJ_PREFIX (proj $($PROJ_PREFIX/bin/proj 2>&1 | head -1 || echo unknown))"
green "✓ Target:    $TARGET_ISA ($MARCH_FLAG)"

if [ "$DO_CLEAN" = 1 ]; then
    say "Clean"
    rm -rf "$BUILD"
fi

say "Configure"
mkdir -p "$BUILD"
cmake -S "$SRC" -B "$BUILD" \
    -G "Unix Makefiles" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DCMAKE_C_COMPILER=icx \
    -DCMAKE_CXX_COMPILER=icpx \
    -DCMAKE_C_FLAGS="-O3 $MARCH_FLAG -fno-fast-math -ffp-contract=off" \
    -DCMAKE_CXX_FLAGS="-O3 $MARCH_FLAG -fno-fast-math -ffp-contract=off" \
    -DCMAKE_PREFIX_PATH="$PROJ_PREFIX" \
    -DPROJ_INCLUDE_DIR="$PROJ_PREFIX/include" \
    -DPROJ_LIBRARY="$PROJ_PREFIX/lib/libproj.so" \
    -DBUILD_TESTING=OFF \
    -DBUILD_SHARED_LIBS=ON \
    -DGDAL_BUILD_OPTIONAL_DRIVERS=OFF \
    -DOGR_BUILD_OPTIONAL_DRIVERS=OFF \
    -DGDAL_USE_INTERNAL_LIBS=WHEN_NO_EXTERNAL \
    >/dev/null
green "✓ Configured (PROJ linked from $PROJ_PREFIX)"

say "Build"
cmake --build "$BUILD" -j"$(nproc)"
green "✓ Built"

say "Install"
cmake --install "$BUILD"
green "✓ Installed to $PREFIX"

say "Verification"
"$PREFIX/bin/gdalinfo" --version 2>&1 || true
"$PREFIX/bin/gdal-config" --version 2>&1
ls "$PREFIX/lib/libgdal.so"* 2>&1 | head -3
green "===== build-gdal.sh DONE ====="
