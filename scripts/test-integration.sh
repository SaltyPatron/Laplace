#!/usr/bin/env bash
# Cross-boundary integration entry — same gate as integration.yml model-roundtrip job.
set -euo pipefail
exec "$(cd "$(dirname "$0")" && pwd)/model-roundtrip-ci.sh"
