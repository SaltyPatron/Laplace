#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source "$root/scripts/lib/database.sh"
export LAPLACE_ENGINE_BUILD="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:?}/Laplace/release}/engine"
export LAPLACE_REUSE_INSTALLED_NATIVE=1
export LAPLACE_PERFCACHE_BIN
LAPLACE_PERFCACHE_BIN=$(find "${LAPLACE_INSTALL_PREFIX:?}/share/laplace" -maxdepth 1 -name 'laplace_t0_perfcache*.bin' -print -quit)
results="${RUNNER_TEMP:-${LAPLACE_SCRATCH_ROOT:?}}/database-name-results"
for project in Laplace.Endpoints.Lichess.Tests Laplace.Chess.Tests; do
  dotnet test "$root/app/$project/$project.csproj" -c Release --nologo \
    -p:LAPLACE_BUILD_ROOT="${LAPLACE_BUILD:?}/laplace-mono/database-names" \
    --filter 'FullyQualifiedName~LichessControlTests|FullyQualifiedName~UciClientTests.ReadinessCarriesTheActualDatabaseIdentity' \
    --logger "trx;LogFileName=$project.trx" --results-directory "$results"
done
python3 - "$results" <<'PY'
from pathlib import Path
import sys, xml.etree.ElementTree as ET
for file in Path(sys.argv[1]).glob('*.trx'):
    counters = ET.parse(file).getroot().find('.//{*}Counters').attrib
    if int(counters['passed']) < 1 or counters['passed'] != counters['total']:
        raise SystemExit(f'All selected tests must execute and pass: {file.name}')
PY
