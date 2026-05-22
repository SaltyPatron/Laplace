#!/bin/bash
# scripts/build-postgis.sh
#
# Build PostGIS 3.6.3 from external/postgis/ (git submodule pinned to
# 3.6.3 per Story B.3 / commit 331e586) with the Intel toolchain
# (icx/icpx, per ADR 0028) against the custom PG 18 built by build-pg.sh.
# Installs into the custom PG's lib/share/extension dirs.
#
# Idempotent. Reuses ./configure on re-run; rebuilds only what changed.
# `--clean` removes generated files for a fresh build.
#
# Usage:
#   scripts/build-postgis.sh              # build + install (idempotent)
#   scripts/build-postgis.sh --clean      # `make clean` + rebuild
#   scripts/build-postgis.sh --configure-only
#
# Prerequisites:
#   - external/postgis submodule initialized
#   - Custom PG 18 already installed (scripts/build-pg.sh run successfully)
#   - System packages: libgeos-dev, libproj-dev, libgdal-dev,
#     libxml2-dev (PostGIS needs it), libjson-c-dev, libsfcgal-dev (optional)
#   - Intel oneAPI 2026
#
# Per Story B.6.

set -euo pipefail

# --- paths + invariants -------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
POSTGIS_SRC="$REPO_DIR/external/postgis"
PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
PROJ_PREFIX="${LAPLACE_PROJ_PREFIX:-/opt/laplace/proj}"
GEOS_PREFIX="${LAPLACE_GEOS_PREFIX:-/opt/laplace/geos}"
GDAL_PREFIX="${LAPLACE_GDAL_PREFIX:-/opt/laplace/gdal}"
TARGET_ISA="${LAPLACE_TARGET_ISA:-AVX2}"

# --- arg parsing --------------------------------------------------------------

DO_CLEAN=0
CONFIGURE_ONLY=0
for arg in "$@"; do
    case "$arg" in
        --clean)            DO_CLEAN=1 ;;
        --configure-only)   CONFIGURE_ONLY=1 ;;
        -h|--help)
            sed -n '2,/^$/p' "$0"
            exit 0
            ;;
        *)
            echo "Unknown flag: $arg" >&2
            exit 64
            ;;
    esac
done

# --- helpers ------------------------------------------------------------------

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "=== $1 ==="; }

# --- preflight ----------------------------------------------------------------

say "Preflight"
[ -d "$POSTGIS_SRC" ] || { red "Missing $POSTGIS_SRC — submodule init'd?"; exit 1; }
[ -x "$PG_PREFIX/bin/pg_config" ] || { red "Custom PG not installed at $PG_PREFIX — run scripts/build-pg.sh first"; exit 1; }

# Source oneAPI for icx/icpx
if [ -f /opt/intel/oneapi/setvars.sh ]; then
    # shellcheck disable=SC1091
    source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1 || true
fi

command -v icx >/dev/null  || { red "icx not in PATH — source oneAPI setvars.sh"; exit 1; }
command -v icpx >/dev/null || { red "icpx not in PATH — source oneAPI setvars.sh"; exit 1; }

# Verify custom-built GEOS/PROJ/GDAL prefixes exist (built by scripts/build-{proj,geos,gdal}.sh)
[ -x "$PROJ_PREFIX/bin/proj" ]         || { red "PROJ not at $PROJ_PREFIX — run scripts/build-proj.sh first"; exit 1; }
[ -x "$GEOS_PREFIX/bin/geos-config" ]  || { red "GEOS not at $GEOS_PREFIX — run scripts/build-geos.sh first"; exit 1; }
[ -x "$GDAL_PREFIX/bin/gdal-config" ]  || { red "GDAL not at $GDAL_PREFIX — run scripts/build-gdal.sh first"; exit 1; }

# Put the custom builds on PATH so PostGIS's configure picks up their
# {geos,gdal}-config tools. PKG_CONFIG_PATH lets configure resolve PROJ.
export PATH="$GEOS_PREFIX/bin:$GDAL_PREFIX/bin:$PROJ_PREFIX/bin:$PATH"
export PKG_CONFIG_PATH="$PROJ_PREFIX/lib/pkgconfig:$GEOS_PREFIX/lib/pkgconfig:$GDAL_PREFIX/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
export LD_LIBRARY_PATH="$PROJ_PREFIX/lib:$GEOS_PREFIX/lib:$GDAL_PREFIX/lib:${LD_LIBRARY_PATH:-}"

# Sanity: pkg-config needs libxml2-dev installed
pkg-config --exists libxml-2.0 || { red "libxml-2.0 not found via pkg-config — install libxml2-dev"; exit 1; }

green "✓ icx:           $(icx --version | head -1)"
green "✓ Custom PG:     $($PG_PREFIX/bin/pg_config --version)"
green "✓ PostGIS src:   $POSTGIS_SRC (commit $(cd "$POSTGIS_SRC" && git rev-parse --short HEAD))"
green "✓ GEOS (custom): $($GEOS_PREFIX/bin/geos-config --version) at $GEOS_PREFIX"
green "✓ PROJ (custom): $(pkg-config --modversion proj) at $PROJ_PREFIX"
green "✓ GDAL (custom): $($GDAL_PREFIX/bin/gdal-config --version) at $GDAL_PREFIX"

# --- compiler flags -----------------------------------------------------------

case "$TARGET_ISA" in
    AVX2)    MARCH_FLAG="-march=haswell" ;;
    AVX512)  MARCH_FLAG="-march=sapphirerapids" ;;
    native)  MARCH_FLAG="-march=native" ;;
    *)       red "Unknown LAPLACE_TARGET_ISA=$TARGET_ISA"; exit 1 ;;
esac

CFLAGS_BASE="-O3 $MARCH_FLAG -fno-fast-math -ffp-contract=off"

# --- autogen (PostGIS uses autoconf; needs autogen.sh to produce configure) ---

if [ ! -f "$POSTGIS_SRC/configure" ] || [ "$DO_CLEAN" = 1 ]; then
    say "autogen.sh (regenerates ./configure from configure.ac)"
    cd "$POSTGIS_SRC"
    ./autogen.sh
    cd "$REPO_DIR"
    green "✓ configure regenerated"
else
    green "✓ configure already present"
fi

# --- clean if requested -------------------------------------------------------

if [ "$DO_CLEAN" = 1 ]; then
    say "make clean (preserves configure output)"
    cd "$POSTGIS_SRC"
    make clean 2>/dev/null || true
    cd "$REPO_DIR"
fi

# --- configure ----------------------------------------------------------------

if [ ! -f "$POSTGIS_SRC/GNUmakefile" ] || [ "$DO_CLEAN" = 1 ]; then
    say "Configure"
    cd "$POSTGIS_SRC"
    ./configure \
        CC=icx CXX=icpx \
        CFLAGS="$CFLAGS_BASE" \
        CXXFLAGS="$CFLAGS_BASE" \
        --with-pgconfig="$PG_PREFIX/bin/pg_config" \
        --with-geosconfig="$GEOS_PREFIX/bin/geos-config" \
        --with-gdalconfig="$GDAL_PREFIX/bin/gdal-config" \
        --with-projdir="$PROJ_PREFIX"
    cd "$REPO_DIR"
    green "✓ Configured against custom PG at $PG_PREFIX"
else
    green "✓ Already configured ($POSTGIS_SRC/GNUmakefile exists; use --clean to redo)"
fi

if [ "$CONFIGURE_ONLY" = 1 ]; then
    green "Stopped after configure (--configure-only)"
    exit 0
fi

# --- build --------------------------------------------------------------------

say "Build (make -j$(nproc))"
cd "$POSTGIS_SRC"
make -j"$(nproc)"
cd "$REPO_DIR"
green "✓ Built"

# --- install ------------------------------------------------------------------

say "Install into $PG_PREFIX (PG's extension dirs)"
# PostGIS's `make install` follows pg_config; installs into our custom PG.
cd "$POSTGIS_SRC"
if [ -w "$PG_PREFIX/lib" ] 2>/dev/null; then
    INSTALL_CMD="make install"
else
    yellow "Need privileged install; using sudo make install"
    INSTALL_CMD="sudo make install"
fi
$INSTALL_CMD
cd "$REPO_DIR"
green "✓ Installed"

# --- verification -------------------------------------------------------------

say "Verification"
ls "$PG_PREFIX/share/postgresql/extension/postgis.control" 2>&1 | head -1
ls "$PG_PREFIX/lib/postgresql/postgis-3.so" 2>&1 | head -1 \
    || ls "$PG_PREFIX/lib/postgis-3.so" 2>&1 | head -1
green "===== build-postgis.sh DONE ====="
