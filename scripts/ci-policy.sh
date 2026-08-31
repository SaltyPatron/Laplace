#!/usr/bin/env bash
# Compatibility alias for the executable policy profile. The policy body lives in
# ci-policy-suite.sh and is invoked only by scripts/test-profile-registry.py.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
exec python3 scripts/test-profile-registry.py run --profile policy
