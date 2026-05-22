#!/bin/bash
# scripts/build-pg.sh
#
# Build PostgreSQL 18 from external/postgresql/ (git submodule pinned to
# REL_18_3 per Story B.2 / commit 331e586) with the Intel toolchain
# (icx/icpx, per ADR 0028) and install to /opt/laplace/pgsql-18/.
#
# Idempotent. Reuses the build dir on re-run; rebuilds only what changed.
# A `--clean` flag forces a clean rebuild.
#
# Usage:
#   scripts/build-pg.sh              # build + install (idempotent)
#   scripts/build-pg.sh --clean      # wipe build dir, rebuild from scratch
#   scripts/build-pg.sh --configure-only   # just run ./configure
#
# Prerequisites (one-time, host-level):
#   - external/postgresql submodule initialized
#   - Intel oneAPI 2026 at /opt/intel/oneapi/
#   - System packages: libicu-dev, libssl-dev, libreadline-dev, zlib1g-dev,
#     libxml2-dev, uuid-dev (e2fs uuid library)
#   - Write access to /opt/laplace/ (or run via sudo for the install step)
#
# Per Story B.5.

set -euo pipefail

# --- paths + invariants -------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PG_SRC="$REPO_DIR/external/postgresql"
PG_BUILD="$REPO_DIR/external/postgresql/build"
PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
TARGET_ISA="${LAPLACE_TARGET_ISA:-AVX2}"   # AVX2 | AVX512 | native

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

require_dir() {
    [ -d "$1" ] || { red "Missing directory: $1"; exit 1; }
}

# --- preflight ----------------------------------------------------------------

say "Preflight"
require_dir "$PG_SRC"
[ -f "$PG_SRC/configure" ] || { red "$PG_SRC/configure not found — submodule init'd?"; exit 1; }

# Source oneAPI so icx/icpx are on PATH + LD_LIBRARY_PATH knows about MKL
if [ -f /opt/intel/oneapi/setvars.sh ]; then
    # shellcheck disable=SC1091
    source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1 || true
fi

command -v icx >/dev/null  || { red "icx not in PATH — source oneAPI setvars.sh"; exit 1; }
command -v icpx >/dev/null || { red "icpx not in PATH — source oneAPI setvars.sh"; exit 1; }

green "✓ icx:  $(icx --version | head -1)"
green "✓ icpx: $(icpx --version | head -1)"
green "✓ PG src: $PG_SRC (commit $(cd "$PG_SRC" && git rev-parse --short HEAD))"
green "✓ Install prefix: $PG_PREFIX"
green "✓ Target ISA: $TARGET_ISA"

# --- clean if requested -------------------------------------------------------

if [ "$DO_CLEAN" = 1 ]; then
    say "Clean build dir"
    rm -rf "$PG_BUILD"
    green "✓ Removed $PG_BUILD"
fi

# --- configure ----------------------------------------------------------------

# Map LAPLACE_TARGET_ISA to compiler march
case "$TARGET_ISA" in
    AVX2)    MARCH_FLAG="-march=haswell" ;;     # AVX2 baseline
    AVX512)  MARCH_FLAG="-march=sapphirerapids" ;;
    native)  MARCH_FLAG="-march=native" ;;
    *)       red "Unknown LAPLACE_TARGET_ISA=$TARGET_ISA (expected AVX2|AVX512|native)"; exit 1 ;;
esac

# Determinism discipline (RULES.md R7): no -ffast-math; explicit FP contract off
CFLAGS_BASE="-O3 $MARCH_FLAG -fno-fast-math -ffp-contract=off"
CXXFLAGS_BASE="$CFLAGS_BASE"

mkdir -p "$PG_BUILD"

if [ ! -f "$PG_BUILD/Makefile" ] || [ "$DO_CLEAN" = 1 ]; then
    say "Configure (./configure)"
    cd "$PG_BUILD"
    # PG's configure must be invoked via path; can use VPATH builds.
    "$PG_SRC/configure" \
        CC=icx CXX=icpx \
        CFLAGS="$CFLAGS_BASE" \
        CXXFLAGS="$CXXFLAGS_BASE" \
        --prefix="$PG_PREFIX" \
        --with-icu \
        --with-ssl=openssl \
        --with-zlib \
        --with-uuid=e2fs \
        --with-libxml \
        --without-readline    # readline is a dev convenience; CI doesn't need it
    cd "$REPO_DIR"
    green "✓ Configured"
else
    green "✓ Already configured ($PG_BUILD/Makefile exists; use --clean to redo)"
fi

if [ "$CONFIGURE_ONLY" = 1 ]; then
    green "Stopped after configure (--configure-only)"
    exit 0
fi

# --- build --------------------------------------------------------------------

say "Build (make -j$(nproc))"
cd "$PG_BUILD"
make -j"$(nproc)"
cd "$REPO_DIR"
green "✓ Built"

# --- install ------------------------------------------------------------------

say "Install to $PG_PREFIX"
# Detect whether we already have write access (running as root or path owned)
if [ -w "$(dirname "$PG_PREFIX")" ] || [ -w "$PG_PREFIX" ] 2>/dev/null; then
    INSTALL_CMD="make install"
else
    yellow "Need privileged install (not writable: $(dirname "$PG_PREFIX")); using sudo make install"
    INSTALL_CMD="sudo make install"
fi
cd "$PG_BUILD"
$INSTALL_CMD
cd "$REPO_DIR"
green "✓ Installed to $PG_PREFIX"

# --- verification -------------------------------------------------------------

say "Verification"
if [ -x "$PG_PREFIX/bin/postgres" ]; then
    "$PG_PREFIX/bin/postgres" --version
    "$PG_PREFIX/bin/pg_config" --version
    "$PG_PREFIX/bin/pg_config" --bindir --libdir --sharedir --pkglibdir --includedir-server | sed 's/^/  /'
    green "✓ Custom PG 18 ready at $PG_PREFIX"
else
    red "✗ Expected binary missing: $PG_PREFIX/bin/postgres"
    exit 1
fi

green "===== build-pg.sh DONE ====="
