#!/usr/bin/env bash
# Verify/provision the pinned host dependency cache without touching product state.
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

section() { echo "::group::$1"; }
endsection() { echo "::endgroup::"; }

section "Workspace and toolchain"
if [[ -d .git/modules && -n "$(ls -A .git/modules 2>/dev/null)" ]]; then
  echo "::error::.git/modules is populated — dependencies must come from $CACHE"
  exit 1
fi
init=$(git submodule status 2>/dev/null | grep -cE '^[ +U]' || true)
[[ "${init:-0}" -eq 0 ]] || { echo "::error::$init submodule(s) initialized"; exit 1; }
[[ -d /opt/intel/oneapi ]] || { echo "::error::oneAPI missing"; exit 1; }
command -v python3 >/dev/null
command -v dotnet >/dev/null
command -v bash >/dev/null
"$PG_PREFIX/bin/pg_config" --version >/dev/null
result=$(psql -d postgres -U laplace_admin -tAc "SELECT current_user || ' on ' || current_database();")
[[ "$result" == "laplace_admin on postgres" ]] || { echo "::error::peer auth failed: $result"; exit 1; }
ucd="${LAPLACE_DATA_ROOT:-/vault/Data}/UCD/Public/UCD/latest/ucdxml/ucd.nounihan.flat.zip"
[[ -f "$ucd" ]] || { echo "::error::UCD zip missing: $ucd"; exit 1; }
endsection

section "Pinned external cache"
if [[ "$CHECK_ONLY" -eq 0 ]]; then
  for directory in tree-sitter geos proj gdal pgsql-18 include lib share bin; do
    mkdir -p "$PREFIX/$directory"
  done
fi
[[ -f "$CACHE/PINS.tsv" ]] || { echo "::error::$CACHE/PINS.tsv missing — run bootstrap"; exit 1; }
miss=0
stale=0
while IFS=$'\t' read -r path _url pin; do
  case "$path" in ''|'#'*) continue;; esac
  entry="$CACHE/${path#external/}"
  if [[ ! -d "$entry/.git" ]]; then
    echo "::error::absent: $entry"
    miss=$((miss + 1))
    continue
  fi
  head=$(git -c safe.directory='*' -C "$entry" rev-parse HEAD 2>/dev/null || true)
  if [[ "$head" != "$pin" ]]; then
    echo "::error::stale: ${path#external/} at ${head:0:12}, pinned ${pin:0:12}"
    stale=$((stale + 1))
  fi
done < "$CACHE/PINS.tsv"
for file in \
  "$PREFIX/proj/lib/libproj.so" \
  "$PREFIX/geos/lib/libgeos_c.so" \
  "$PREFIX/gdal/lib/libgdal.so" \
  "$PG_PREFIX/bin/postgres" \
  "$PG_PREFIX/lib/postgis-3.so" \
  "$PREFIX/tree-sitter/lib/libtree-sitter.a"; do
  [[ -e "$file" ]] || { echo "::error::unbuilt: $file"; miss=$((miss + 1)); }
done
[[ "$miss" -eq 0 && "$stale" -eq 0 ]] || exit 1
cfg=$("$PG_PREFIX/bin/pg_config" --configure 2>/dev/null || true)
need_rebuild=0
for flag in --with-lz4 --with-zstd --with-liburing; do
  case "$cfg" in
    *"$flag"*) ;;
    *)
      if [[ "$CHECK_ONLY" -eq 1 ]]; then
        echo "::error::postgres dependency lacks required $flag"
      else
        echo "::warning::postgres lacks $flag — forcing rebuild"
      fi
      need_rebuild=1
      ;;
  esac
done
if [[ "$CHECK_ONLY" -eq 1 ]]; then
  [[ "$need_rebuild" -eq 0 ]] || exit 1
  echo "DEPENDENCY_PROOF_OK mode=read-only"
  endsection
  exit 0
fi
[[ "$need_rebuild" -eq 0 ]] || export LAPLACE_FORCE_DEPS=1
endsection

section "Build pinned dependencies"
umask 0002
bash scripts/build-system-deps.sh
endsection

section "Installed dependency provenance"
miss=0
cfg=$("$PG_PREFIX/bin/pg_config" --configure 2>/dev/null || true)
for flag in --with-lz4 --with-zstd --with-liburing; do
  case "$cfg" in *"$flag"*) ;; *) echo "::error::postgres still lacks $flag"; miss=$((miss + 1));; esac
done
for file in \
  "$PREFIX/proj/lib/libproj.so" \
  "$PREFIX/geos/lib/libgeos_c.so" \
  "$PREFIX/gdal/lib/libgdal.so" \
  "$PG_PREFIX/bin/postgres" \
  "$PG_PREFIX/lib/postgis-3.so" \
  "$PREFIX/tree-sitter/lib/libtree-sitter.a"; do
  [[ -e "$file" ]] || { echo "::error::missing artifact: $file"; miss=$((miss + 1)); }
done
[[ "$miss" -eq 0 ]]
endsection
