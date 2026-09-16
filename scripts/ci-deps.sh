#!/usr/bin/env bash
# Development consumes installed dependencies. Explicit operator setup may build them.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
PG_PREFIX="${LAPLACE_PG_PREFIX:-$PREFIX/pgsql-18}"
CACHE="${LAPLACE_EXTERNAL:-/build/external}"
CHECK_ONLY=0
case "${1:-}" in
  "") ;;
  --check-only) CHECK_ONLY=1 ;;
  *) echo "usage: $0 [--check-only]" >&2; exit 2 ;;
esac

if [[ "$CHECK_ONLY" == 0 ]]; then
  python3 scripts/postgresql-release.py prepare-source --external "$CACHE"
  bash scripts/build-system-deps.sh
fi

command -v bash >/dev/null
command -v python3 >/dev/null
command -v dotnet >/dev/null
command -v ninja >/dev/null
[[ -d /opt/intel/oneapi ]] || { echo "::error::oneAPI missing" >&2; exit 1; }
[[ -x "$PG_PREFIX/bin/pg_config" && -x "$PG_PREFIX/bin/postgres" ]] || {
  echo "::error::PostgreSQL runtime missing under $PG_PREFIX" >&2; exit 1;
}

# Build inputs must exist; CMake/Ninja own source-change detection.
for directory in postgresql postgis proj geos gdal tree-sitter blake3; do
  [[ -e "$CACHE/$directory" ]] || { echo "::error::dependency source missing: $CACHE/$directory" >&2; exit 1; }
done

for artifact in \
  "$PREFIX/proj/lib/libproj.so" \
  "$PREFIX/geos/lib/libgeos_c.so" \
  "$PREFIX/gdal/lib/libgdal.so" \
  "$PG_PREFIX/lib/postgis-3.so" \
  "$PREFIX/tree-sitter/lib/libtree-sitter.a"; do
  [[ -e "$artifact" ]] || { echo "::error::installed dependency missing: $artifact" >&2; exit 1; }
done

cfg=$("$PG_PREFIX/bin/pg_config" --configure)
for flag in --with-lz4 --with-zstd --with-liburing; do
  [[ "$cfg" == *"$flag"* ]] || { echo "::error::PostgreSQL missing required $flag" >&2; exit 1; }
done

peer=$("$PG_PREFIX/bin/psql" -X -w -h /var/run/postgresql -U laplace_admin -d postgres -tAc \
  "SELECT current_user || ' on ' || current_database();")
[[ "$peer" == "laplace_admin on postgres" ]] || { echo "::error::PostgreSQL peer auth failed: $peer" >&2; exit 1; }

ucd="${LAPLACE_DATA_ROOT:-/vault/Data}/UCD/Public/UCD/latest/ucdxml/ucd.nounihan.flat.zip"
[[ -f "$ucd" ]] || { echo "::error::UCD input missing: $ucd" >&2; exit 1; }

echo "DEPENDENCIES_READY mode=$([[ "$CHECK_ONLY" == 1 ]] && echo consume || echo provision) postgres=$($PG_PREFIX/bin/pg_config --version)"
