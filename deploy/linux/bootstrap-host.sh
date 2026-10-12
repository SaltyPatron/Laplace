#!/usr/bin/env bash
# Compatibility entry point; the monorepo has one service installation owner.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$HERE/site-host.sh" "$@"
