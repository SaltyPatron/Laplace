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

# System deps for PostGIS
require_lib() {
    pkg-config --exists "$1" 2>/dev/null \
        || { red "Missing pkg-config dependency: $1 — install libX-dev for it"; exit 1; }
}
require_lib geos      || true   # PostGIS uses geos-config not pkg-config
require_lib proj
require_lib libxml-2.0
command -v geos-config >/dev/null || { red "geos-config not on PATH (libgeos-dev not installed?)"; exit 1; }
command -v gdal-config >/dev/null || { red "gdal-config not on PATH (libgdal-dev not installed?)"; exit 1; }

green "✓ icx:           $(icx --version | head -1)"
green "✓ Custom PG:     $($PG_PREFIX/bin/pg_config --version)"
green "✓ PostGIS src:   $POSTGIS_SRC (commit $(cd "$POSTGIS_SRC" && git rev-parse --short HEAD))"
green "✓ geos:          $(geos-config --version)"
green "✓ proj:          $(pkg-config --modversion proj)"
green "✓ gdal:          $(gdal-config --version)"

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
        --with-geosconfig="$(command -v geos-config)" \
        --with-gdalconfig="$(command -v gdal-config)" \
        --with-projdir=/usr        # PROJ system install
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
