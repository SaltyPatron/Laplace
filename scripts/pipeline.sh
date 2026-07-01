#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

MODE=hot
SKIP_CLEAN=0
SKIP_BUILD=0
SKIP_SEED=0
SKIP_PUBLISH=0
SKIP_INSTALL=0
FRESH_DB=0
FORCE_FOUNDATION=0

LAPLACE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-/usr/lib/postgresql/18}"
LAPLACE_EXTERNAL="${LAPLACE_EXTERNAL:-/opt/laplace/external}"
PGDATABASE="${PGDATABASE:-laplace}"

PYTHON=""
if command -v python3 >/dev/null 2>&1; then
  PYTHON=python3
elif command -v python >/dev/null 2>&1; then
  PYTHON=python
else
  echo "::error::python3 not found — install python3 on the runner (hart-server)" >&2
  exit 127
fi

usage() {
  cat <<EOF
Usage: $0 --mode hot|fresh|build-only|provision [options]

Modes:
  build-only  codegen + cmake build + perfcache + dotnet build (no install/deploy)
  provision   install + migrate + extension sync + perfcache GUC (requires build/)
  hot         build-only + provision + publish + ensure-foundation
  fresh       optional clean + build-only + provision (nuke) + publish + force foundation

Options:
  --skip-clean        keep existing build/ tree (fresh mode only)
  --skip-build        skip cmake/dotnet build
  --skip-install      skip cmake --install (build-only mode only)
  --skip-seed         skip ensure-foundation
  --skip-publish      skip deploy/linux/deploy.sh
  --fresh-db          nuke DB before migrate (also set by --mode fresh)
  --force-foundation  pass --force to ensure-foundation.sh
EOF
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:?}"; shift 2 ;;
    --skip-clean) SKIP_CLEAN=1; shift ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --skip-install) SKIP_INSTALL=1; shift ;;
    --skip-seed) SKIP_SEED=1; shift ;;
    --skip-publish) SKIP_PUBLISH=1; shift ;;
    --fresh-db) FRESH_DB=1; shift ;;
    --force-foundation) FORCE_FOUNDATION=1; shift ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

case "$MODE" in
  hot|fresh|build-only|provision) ;;
  *) echo "invalid --mode: $MODE" >&2; exit 2 ;;
esac

if [[ "$MODE" == "fresh" ]]; then
  FRESH_DB=1
  FORCE_FOUNDATION=1
fi

phase_codegen() {
  echo "===== PHASE — CODEGEN ====="
  "$PYTHON" "$ROOT/scripts/codegen-attestation-law.py"
}

phase_clean() {
  echo "===== PHASE — CLEAN ====="
  rm -rf "$ROOT/build"
}

phase_build() {
  phase_codegen
  echo "===== PHASE — BUILD ENGINE + EXTENSIONS ====="
  local data_root="${LAPLACE_DATA_ROOT:-/vault/Data}"
  local ucd="${LAPLACE_UCD_PATH:-$data_root/UCD/Public/UCD/latest}"
  cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
    -DCMAKE_INSTALL_PREFIX="$LAPLACE_INSTALL_PREFIX" \
    -DLAPLACE_PG_PREFIX="$LAPLACE_PG_PREFIX" \
    -DLAPLACE_EXTERNAL="$LAPLACE_EXTERNAL" \
    -DLAPLACE_INSTALL_STAGED=ON \
    -DLAPLACE_UCD_PATH="$ucd" \
    -DLAPLACE_UCDXML_ZIP="$ucd/ucdxml/ucd.nounihan.flat.zip" \
    -DLAPLACE_DUCET_FILE="$ucd/uca/allkeys.txt" \
    -DLAPLACE_UCD_CONFORMANCE_DIR="$ucd/ucd"
  LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}" \
    cmake --build build
  cmake --build build --target laplace_t0_perfcache
  echo "===== PHASE — BUILD APP ====="
  ( cd "$ROOT/app" && dotnet build Laplace.slnx -c Release )
  dotnet build app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj -c Release
  dotnet build app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj -c Release
}

phase_install() {
  echo "===== PHASE — INSTALL ====="
  test -d build || { echo "::error::build/ missing — run build-only first"; exit 1; }
  umask 0002
  cmake --install build
  test -f "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so"
}

phase_migrate() {
  echo "===== PHASE — MIGRATE ($PGDATABASE) ====="
  dotnet build "$ROOT/app/Laplace.Migrations/Laplace.Migrations.csproj" -c Release
  local mig="$ROOT/app/Laplace.Migrations/bin/Release/net10.0/Laplace.Migrations.dll"
  if [[ "$FRESH_DB" -eq 1 ]]; then
    dotnet "$mig" nuke --yes
  fi
  dotnet "$mig" up
}

phase_sync_extension() {
  echo "===== PHASE — SYNC EXTENSION SQL ====="
  local avail installed share
  avail=$(psql -d "$PGDATABASE" -U laplace_admin -tAX \
    -c "SELECT default_version FROM pg_available_extensions WHERE name='laplace_substrate'" \
    | tr -d '[:space:]')
  installed=$(psql -d "$PGDATABASE" -U laplace_admin -tAX \
    -c "SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'" \
    | tr -d '[:space:]')
  echo "laplace_substrate: installed='$installed' available='$avail'"
  test -n "$avail" || { echo "::error::laplace_substrate missing from pg_available_extensions"; exit 1; }
  if [[ -z "$installed" ]]; then
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate"
  elif [[ "$installed" != "$avail" ]]; then
    share=$(dirname "$(find "$LAPLACE_INSTALL_PREFIX" -name laplace_substrate.control -not -path '*/build*' 2>/dev/null | head -1)")
    test -n "$share" || { echo "::error::could not locate laplace_substrate.control under $LAPLACE_INSTALL_PREFIX"; exit 1; }
    cp "$share/laplace_substrate_upgrade.sql" "$share/laplace_substrate--${installed}--${avail}.sql"
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "ALTER EXTENSION laplace_substrate UPDATE TO '$avail'"
    echo "OK upgraded laplace_substrate $installed -> $avail in place"
  else
    echo "OK laplace_substrate already at $avail"
  fi
}

phase_perfcache_guc() {
  echo "===== PHASE — PERFCACHE GUC ====="
  local bin
  bin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$bin" || { echo "::error::perfcache blob not installed under $LAPLACE_INSTALL_PREFIX/share/laplace"; exit 1; }
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "LOAD 'laplace_substrate'" \
    -c "ALTER SYSTEM SET laplace_substrate.perfcache_path = '$bin'" \
    -c "SELECT pg_reload_conf()"
  echo "perfcache_path -> $bin"
}

phase_api_env() {
  echo "===== PHASE — API ENV ====="
  local env_file="$LAPLACE_INSTALL_PREFIX/app/laplace-api.env"
  local bin example
  bin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$bin" || { echo "::error::perfcache blob missing for API env"; exit 1; }
  example="$ROOT/deploy/linux/laplace-api.env.example"
  if [[ ! -f "$env_file" ]]; then
    install -m 0640 -o laplace-runner -g laplace-runner "$example" "$env_file" 2>/dev/null \
      || cp "$example" "$env_file"
    echo "created $env_file from example"
  fi
  if grep -q '^LAPLACE_PERFCACHE_BIN=' "$env_file"; then
    sed -i "s|^LAPLACE_PERFCACHE_BIN=.*|LAPLACE_PERFCACHE_BIN=$bin|" "$env_file"
  else
    printf '\nLAPLACE_PERFCACHE_BIN=%s\n' "$bin" >> "$env_file"
  fi
  echo "LAPLACE_PERFCACHE_BIN -> $bin"
}

phase_publish() {
  echo "===== PHASE — PUBLISH API + SPA ====="
  bash "$ROOT/deploy/linux/deploy.sh"
}

phase_ensure_foundation() {
  echo "===== PHASE — ENSURE FOUNDATION ====="
  local args=()
  [[ "$FORCE_FOUNDATION" -eq 1 ]] && args+=(--force)
  LAPLACE_DBNAME="$PGDATABASE" \
    bash "$ROOT/scripts/ensure-foundation.sh" "${args[@]}"
}

phase_provision() {
  [[ "$SKIP_INSTALL" -eq 0 ]] && phase_install
  phase_migrate
  phase_sync_extension
  phase_perfcache_guc
  phase_api_env
}

if [[ "$SKIP_CLEAN" -eq 0 && "$MODE" == "fresh" ]]; then
  phase_clean
fi

if [[ "$MODE" == "build-only" ]]; then
  phase_build
  [[ "$SKIP_INSTALL" -eq 0 ]] && phase_install
  echo "===== PIPELINE COMPLETE (build-only) ====="
  exit 0
fi

if [[ "$MODE" == "provision" ]]; then
  phase_provision
  echo "===== PIPELINE COMPLETE (provision) ====="
  exit 0
fi

if [[ "$SKIP_BUILD" -eq 0 ]]; then
  phase_build
fi

phase_provision
[[ "$SKIP_SEED" -eq 0 ]] && phase_ensure_foundation
[[ "$SKIP_PUBLISH" -eq 0 ]] && phase_publish

echo "===== PIPELINE COMPLETE ($MODE) ====="
