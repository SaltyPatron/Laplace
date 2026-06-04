#!/usr/bin/env bash
# Cross-boundary integration entry — same gate as integration.yml model-synthesize job.
set -euo pipefail
exec "$(cd "$(dirname "$0")" && pwd)/model-synthesize-ci.sh"
