#!/usr/bin/env bash
# Verify installed GIN maintenance with owner and restricted database roles.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source "$root/scripts/lib/database.sh"
prefix="${LAPLACE_INSTALL_PREFIX:?}"
export LAPLACE_TIER0="$prefix/share/laplace/tier0.bin"
export LAPLACE_PERFCACHE_BIN="$LAPLACE_TIER0"
export LAPLACE_HIGHWAY="$prefix/share/laplace/highway.bin"
[[ -s "$LAPLACE_TIER0" && -s "$LAPLACE_HIGHWAY" ]] || {
  echo "Installed canonical caches are missing under $prefix/share/laplace" >&2; exit 1;
}
source "$root/deploy/linux/host-lock.sh"
# One completion check at a time against the deployed database; installation holds the host lock exclusively.
locks="${LAPLACE_LOCKS:-/run/lock/laplace}"
host_lock_hold exclusive 9 "$locks/ingest-completion.lock" "${LAPLACE_HOST_LOCK_WAIT_SECONDS:-600}"
host_lock_hold shared 8 "${LAPLACE_HOST_LOCK:-$locks/host-resource.lock}" "${LAPLACE_HOST_LOCK_WAIT_SECONDS:-600}"
major=$(pg_config --version | sed -E 's/^PostgreSQL ([0-9]+).*/\1/')
export PGOPTIONS="-c dynamic_library_path=$prefix/lib/postgresql/$major:\$libdir -c extension_control_path=$prefix/share/postgresql/$major:\$system"
export LAPLACE_DB="$LAPLACE_DB;Options=$PGOPTIONS"
export LAPLACE_REUSE_INSTALLED_NATIVE=1
export LAPLACE_NATIVE_TEST_LIBRARY="$prefix/lib/liblaplace.so"
export LAPLACE_ENGINE_BUILD="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:?}/Laplace/release}/engine"
site_build="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:?}/Laplace/release}"
if [[ ! -s "$site_build/csharp/LaplaceNative.g.cs" ]]; then
  native_src="${LAPLACE_NATIVE:-/repos/src/Laplace-Native}"
  [[ -d "$native_src" ]] || native_src="$(dirname "$root")/Laplace-Native"
  mkdir -p "$site_build/csharp"
  cmake -DCC=icx -DNODE="$(command -v node)" -DSOURCE="$native_src" -DOUT="$site_build/csharp" -P "$native_src/tools/csharp/generate.cmake" 8>&- 9>&-
fi
export LaplaceNativeGenerated="$site_build/csharp"
output="${RUNNER_TEMP:-${LAPLACE_SCRATCH_ROOT:?}}/ingest-completion-build"
results="${RUNNER_TEMP:-${LAPLACE_SCRATCH_ROOT:?}}/ingest-completion-results"
mkdir -p "$results"
psql -X -v ON_ERROR_STOP=1 -c "SELECT public.laplace_ensure_stage();" >/dev/null
invalid_idx="$(psql -X -v ON_ERROR_STOP=1 -tAc "
  SELECT count(*)
  FROM pg_index i
  JOIN pg_class c ON c.oid = i.indexrelid
  JOIN pg_class t ON t.oid = i.indrelid
  JOIN pg_namespace n ON n.oid = t.relnamespace
  WHERE n.nspname = 'public'
    AND t.relname IN ('entity', 'physicality', 'attestation', 'consensus')
    AND (NOT i.indisvalid OR NOT i.indisready);")"
[[ "${invalid_idx:-0}" == "0" ]] || {
  echo "Invalid canonical substrate index count: ${invalid_idx}" >&2
  exit 1
}
dotnet test "$root/app/Laplace.Data.Tests/Laplace.Data.Tests.csproj" \
  -c Release --nologo -p:LAPLACE_BUILD_ROOT="$output" \
  --filter 'FullyQualifiedName~ReadPathArchitectureGateTests' \
  --logger 'trx;LogFileName=native-reads.trx' --results-directory "$results" 8>&- 9>&-
python3 - "$results/native-reads.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
c = ET.parse(sys.argv[1]).getroot().find('.//{*}Counters')
if c is None or int(c.attrib['total']) < 1 or c.attrib['passed'] != c.attrib['total']:
    raise SystemExit(f"All installed Native read-path architecture gates must execute and pass: {c.attrib if c is not None else None}")
PY
