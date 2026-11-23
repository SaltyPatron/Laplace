#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source "$root/scripts/lib/database.sh"
export LAPLACE_ENGINE_BUILD="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:?}/Laplace/release}/engine"
site_build="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:?}/Laplace/release}"
if [[ ! -s "$site_build/csharp/LaplaceNative.g.cs" ]]; then
  native_src="${LAPLACE_NATIVE:-/repos/src/Laplace-Native}"
  [[ -d "$native_src" ]] || native_src="$(dirname "$root")/Laplace-Native"
  mkdir -p "$site_build/csharp"
  cmake -DCC=icx -DNODE="$(command -v node)" -DSOURCE="$native_src" -DOUT="$site_build/csharp" -P "$native_src/tools/csharp/generate.cmake"
fi
export LaplaceNativeGenerated="$site_build/csharp"
export LAPLACE_REUSE_INSTALLED_NATIVE=1
export LAPLACE_NATIVE_TEST_LIBRARY="${LAPLACE_INSTALL_PREFIX:?}/lib/liblaplace.so"
export LAPLACE_TIER0="${LAPLACE_INSTALL_PREFIX:?}/share/laplace/tier0.bin"
export LAPLACE_PERFCACHE_BIN="$LAPLACE_TIER0"
export LAPLACE_HIGHWAY="$LAPLACE_INSTALL_PREFIX/share/laplace/highway.bin"
[[ -s "$LAPLACE_TIER0" && -s "$LAPLACE_HIGHWAY" ]] || {
  echo "Installed canonical caches are missing under $LAPLACE_INSTALL_PREFIX/share/laplace" >&2; exit 1;
}
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
