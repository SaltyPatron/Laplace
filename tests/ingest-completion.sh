#!/usr/bin/env bash
# Verify both owner maintenance and restricted-role completion on a recreated database.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source "$root/scripts/lib/database.sh"
prefix="${LAPLACE_INSTALL_PREFIX:?}"
source "$root/deploy/linux/host-lock.sh"
host_lock_hold exclusive 8 "${LAPLACE_HOST_LOCK:-${LAPLACE_LOCKS:-/run/lock/laplace}/host-resource.lock}" "${LAPLACE_HOST_LOCK_WAIT_SECONDS:-600}"
existing=$(psql -X -Atq -d postgres -c "SELECT count(*) FROM pg_database WHERE datname='laplace_substratecrud_test'")
[[ "$existing" == 0 ]] || { echo 'Completion test database is already in use; refusing to replace it.' >&2; exit 1; }
major=$(pg_config --version | sed -E 's/^PostgreSQL ([0-9]+).*/\1/')
export PGOPTIONS="-c dynamic_library_path=$prefix/lib/postgresql/$major:\$libdir -c extension_control_path=$prefix/share/postgresql/$major:\$system -c session_preload_libraries=laplace_substrate"
export LAPLACE_DB="$LAPLACE_DB;Options=$PGOPTIONS"
export LAPLACE_REUSE_INSTALLED_NATIVE=1
export LAPLACE_ENGINE_BUILD="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:?}/Laplace/release}/engine"
export LAPLACE_PERFCACHE_BIN
LAPLACE_PERFCACHE_BIN=$(find "$prefix/share/laplace" -maxdepth 1 -name 'laplace_t0_perfcache*.bin' -print -quit)
[[ -f "$LAPLACE_PERFCACHE_BIN" ]]
output="${LAPLACE_BUILD:?}/laplace-mono/ingest-completion"
results="${RUNNER_TEMP:-${LAPLACE_SCRATCH_ROOT:?}}/ingest-completion-results"
dotnet test "$root/app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj" \
  -c Release --nologo -p:LAPLACE_BUILD_ROOT="$output" \
  --filter 'FullyQualifiedName~WriterCompletionTests' \
  --logger 'trx;LogFileName=completion.trx' --results-directory "$results" 8>&-
python3 - "$results/completion.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
counters = root.find('.//{*}Counters')
if counters is None or int(counters.attrib['passed']) != 2 or int(counters.attrib['total']) != 2:
    raise SystemExit('Both completion tests must execute and pass.')
PY
[[ "$(psql -X -Atq -d postgres -c "SELECT count(*) FROM pg_database WHERE datname='laplace_substratecrud_test'")" == 0 ]]
