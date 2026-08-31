#!/usr/bin/env bash
# Compatibility alias for the executable policy profile. Individual policy suites live
# in scripts/test-profiles.json so selection and receipts are explicit before execution.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
exec python3 scripts/test-profile-registry.py run --profile policy
