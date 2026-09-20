#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_browser_dev() {
  local stamp lock_hash previous selected suite
  mkdir -p build/.stamps
  lock_hash=$(sha256sum web/package-lock.json | awk '{print $1}')
  stamp=build/.stamps/npm-lock
  previous=$(cat "$stamp" 2>/dev/null || true)
  if [[ ! -d web/node_modules || "$previous" != "$lock_hash" ]]; then
    (cd web && npm ci --no-audit --no-fund --prefer-offline)
    printf '%s\n' "$lock_hash" > "$stamp"
  fi
  (cd web && npm run typecheck)
  selected="${LAPLACE_BROWSER_TEST_SUITES:-all}"
  [[ "$selected" != all ]] || selected="read-resource,workspace-ui,data-ui,chess-ui"
  IFS=',' read -ra suites <<< "$selected"
  for suite in "${suites[@]}"; do
    case "$suite" in
      ""|typecheck) ;;
      read-resource) (cd web && npm run test:read-resource) ;;
      workspace-ui) (cd web && npm run test:workspace-ui) ;;
      data-ui) (cd web && npm run test:data-ui) ;;
      chess-ui) (cd web && node scripts/test-chess-ui.mjs) ;;
      *) echo "unknown browser test suite: $suite" >&2; return 2 ;;
    esac
  done
}

run_browser_dev
