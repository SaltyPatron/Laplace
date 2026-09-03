#!/usr/bin/env bash
# Same-repository pull-request proof. Source/build only: no install, migration,
# extension synchronization, service control, database mutation, or publication.
# Runtime compatibility with /opt/laplace is intentionally proved only after the
# exact revision has been installed by the main delivery lane.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Test selection belongs to the executable profile registry. Policy and DEV/BAT
# therefore use the same authority as main; UCI/browser coverage is part of DEV/BAT
# and must not be repeated here with independent commands.
python3 scripts/check-sql-manifest-dependencies.py
bash scripts/test-parallel.sh --policy

bash scripts/pipeline.sh build
bash scripts/test-parallel.sh --engine --all

echo "PR_PROOF_OK policy=green build=green dev_bat=green production_mutations=0"
