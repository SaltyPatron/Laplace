#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
EXPECTED="${1:-$(git -C "$ROOT" rev-parse HEAD)}"
RECEIPT="$APP_DIR/.laplace-source-revision"

[[ "$EXPECTED" =~ ^[0-9a-f]{40}$ ]] || {
  echo "::error::invalid expected application revision: $EXPECTED" >&2
  exit 2
}

ACTUAL="$(cat "$RECEIPT" 2>/dev/null || true)"
if [[ "$ACTUAL" != "$EXPECTED" ]]; then
  echo "::error::deployed application does not belong to this checkout (expected $EXPECTED, found ${ACTUAL:-missing})" >&2
  exit 1
fi

printf 'PASS: deployed application revision %s\n' "$ACTUAL"
