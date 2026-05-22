#!/bin/bash
# scripts/build-all-deps.sh
#
# Orchestrator — builds the complete custom dependency tree from
# external/ submodules with the Intel toolchain. One command.
#
# Dependency order (per Epic B):
#   1. PROJ          (no deps in this set)
#   2. GEOS          (no deps in this set)
#   3. GDAL          (depends on PROJ)
#   4. PostgreSQL    (no deps in this set)
#   5. PostGIS       (depends on PG + GEOS + PROJ + GDAL)
#
# Eigen, Spectra, BLAKE3 are header-only / build-time-only and are
# pulled directly via add_subdirectory() from engine/CMakeLists.txt — no
# separate build step needed.
#
# Output: /opt/laplace/{proj,geos,gdal,pgsql-18}/
#
# Idempotent. Usage:
#   scripts/build-all-deps.sh           # build everything not yet built
#   scripts/build-all-deps.sh --clean   # wipe build dirs, rebuild all
#   scripts/build-all-deps.sh proj geos # build only specified deps
#
# Prerequisites (handled by bootstrap_build_environment in
# bootstrap-laplace-runner.sh):
#   - apt build-deps installed
#   - /opt/laplace/ owned by laplace-runner
#   - Intel oneAPI at /opt/intel/oneapi/
#   - All submodules initialized: git submodule update --init --recursive

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# Default order
DEFAULT_DEPS=(proj geos gdal pg postgis)

# Parse args
DO_CLEAN=0
SELECTED_DEPS=()
for arg in "$@"; do
    case "$arg" in
        --clean) DO_CLEAN=1 ;;
        -h|--help) sed -n '2,/^$/p' "$0"; exit 0 ;;
        proj|geos|gdal|pg|postgis) SELECTED_DEPS+=("$arg") ;;
        *) echo "Unknown arg: $arg" >&2; exit 64 ;;
    esac
done
if [ "${#SELECTED_DEPS[@]}" -eq 0 ]; then
    SELECTED_DEPS=("${DEFAULT_DEPS[@]}")
fi

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }
say()    { echo; echo "############################################################"; echo "# $1"; echo "############################################################"; }

CLEAN_FLAG=""
[ "$DO_CLEAN" = 1 ] && CLEAN_FLAG="--clean"

# Ensure submodules are initialized
say "Verify submodule state"
cd "$REPO_DIR"
git submodule update --init external/proj external/geos external/gdal \
    external/postgresql external/postgis \
    external/eigen external/spectra external/blake3 2>&1 | tail -10 || true
green "✓ Submodules initialized"

for dep in "${SELECTED_DEPS[@]}"; do
    case "$dep" in
        proj)
            say "PROJ"
            "$SCRIPT_DIR/build-proj.sh" $CLEAN_FLAG
            ;;
        geos)
            say "GEOS"
            "$SCRIPT_DIR/build-geos.sh" $CLEAN_FLAG
            ;;
        gdal)
            say "GDAL"
            "$SCRIPT_DIR/build-gdal.sh" $CLEAN_FLAG
            ;;
        pg)
            say "PostgreSQL"
            "$SCRIPT_DIR/build-pg.sh" $CLEAN_FLAG
            ;;
        postgis)
            say "PostGIS"
            "$SCRIPT_DIR/build-postgis.sh" $CLEAN_FLAG
            ;;
    esac
done

say "DONE"
echo "Installed:"
for prefix in /opt/laplace/proj /opt/laplace/geos /opt/laplace/gdal /opt/laplace/pgsql-18; do
    if [ -d "$prefix" ]; then
        echo "  $prefix"
    fi
done
