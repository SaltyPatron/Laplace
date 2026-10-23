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
# Serialize this fixed-name test database separately. Library readers may run
# together; only installation needs the exclusive host lock.
locks="${LAPLACE_LOCKS:-/run/lock/laplace}"
host_lock_hold exclusive 9 "$locks/ingest-completion.lock" "${LAPLACE_HOST_LOCK_WAIT_SECONDS:-600}"
host_lock_hold shared 8 "${LAPLACE_HOST_LOCK:-$locks/host-resource.lock}" "${LAPLACE_HOST_LOCK_WAIT_SECONDS:-600}"
existing=$(psql -X -Atq -d postgres -c "SELECT count(*) FROM pg_database WHERE datname='laplace_substratecrud_test'")
[[ "$existing" == 0 ]] || { echo 'Completion test database is already in use; refusing to replace it.' >&2; exit 1; }
major=$(pg_config --version | sed -E 's/^PostgreSQL ([0-9]+).*/\1/')
export PGOPTIONS="-c dynamic_library_path=$prefix/lib/postgresql/$major:\$libdir -c extension_control_path=$prefix/share/postgresql/$major:\$system -c session_preload_libraries=laplace_substrate"
export LAPLACE_DB="$LAPLACE_DB;Options=$PGOPTIONS"
export LAPLACE_REUSE_INSTALLED_NATIVE=1
export LAPLACE_ENGINE_BUILD="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:?}/Laplace/release}/engine"
output="${LAPLACE_BUILD:?}/laplace-mono/ingest-completion"
results="${RUNNER_TEMP:-${LAPLACE_SCRATCH_ROOT:?}}/ingest-completion-results"
dotnet test "$root/app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj" \
  -c Release --nologo -p:LAPLACE_BUILD_ROOT="$output" \
  --filter 'FullyQualifiedName~WriterCompletionTests' \
  --logger 'trx;LogFileName=completion.trx' --results-directory "$results" 8>&- 9>&-
python3 - "$results/completion.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
counters = root.find('.//{*}Counters')
if counters is None or int(counters.attrib['passed']) != 2 or int(counters.attrib['total']) != 2:
    raise SystemExit('Both completion tests must execute and pass.')
PY
[[ "$(psql -X -Atq -d postgres -c "SELECT count(*) FROM pg_database WHERE datname='laplace_substratecrud_test'")" == 0 ]]
