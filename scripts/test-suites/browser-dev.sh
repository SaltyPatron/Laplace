#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_browser_dev() {
  local stamp lock_hash previous
  mkdir -p build/.stamps
  lock_hash=$(sha256sum web/package-lock.json | awk '{print $1}')
  stamp=build/.stamps/npm-lock
  previous=$(cat "$stamp" 2>/dev/null || true)
  if [[ ! -d web/node_modules || "$previous" != "$lock_hash" ]]; then
    (cd web && npm ci --no-audit --no-fund --prefer-offline)
    printf '%s\n' "$lock_hash" > "$stamp"
  fi
  (cd web && npm run typecheck && npm run test:chess-ui)
}

run_browser_dev
