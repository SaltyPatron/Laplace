#!/usr/bin/env bash
# Point this checkout at scripts/githooks (shared across worktrees via .git).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HOOKS="$ROOT/scripts/githooks"

chmod +x "$HOOKS/pre-commit" 2>/dev/null || true
git -C "$ROOT" config core.hooksPath scripts/githooks
echo ">>> core.hooksPath=scripts/githooks (pre-commit auto-refreshes docs/INVENTORY.md)"
