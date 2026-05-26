#!/usr/bin/env bash
# Cross-boundary integration entry — same gate as integration.yml model-codec job.
set -euo pipefail
exec "$(cd "$(dirname "$0")" && pwd)/model-codec-ci.sh"
