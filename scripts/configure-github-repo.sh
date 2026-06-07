#!/bin/bash

set -euo pipefail

REPO="${REPO:-SaltyPatron/Laplace}"

err() { printf '\033[0;31m%s\033[0m\n' "$*" >&2; }
ok()  { printf '\033[0;32m%s\033[0m\n' "$*"; }

if ! command -v gh >/dev/null; then
    err "gh CLI not found"; exit 1
fi
if ! gh auth status >/dev/null 2>&1; then
    err "gh not authenticated; run: gh auth login"; exit 1
fi

declare -A vars=(
    [LAPLACE_EXTERNAL]="/opt/laplace/external"
    [LAPLACE_INSTALL_PREFIX]="/opt/laplace"
    [LAPLACE_PG_PREFIX]="/usr/lib/postgresql/18"
)

for name in "${!vars[@]}"; do
    val="${vars[$name]}"
    if gh variable set "$name" --body "$val" --repo "$REPO" >/dev/null 2>&1; then
        ok "✓ $name = $val"
    else
        err "✗ failed to set $name (insufficient scope? need admin or maintainer)"
        exit 1
    fi
done

echo
ok "All Laplace workflow variables pushed to $REPO."
echo "See: https://github.com/$REPO/settings/variables/actions"
echo
echo "Override per-run via 'gh workflow run integration.yml -f LAPLACE_PG_PREFIX=...'"
echo "(once workflow_dispatch inputs are wired) or by editing the variable above."
