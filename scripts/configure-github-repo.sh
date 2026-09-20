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
    # The pinned source cache lives on /build (its own LV) so compile churn stays
    # off the database device. This value becomes vars.LAPLACE_EXTERNAL, which
    # OVERRIDES the workflow default — leaving it at /opt/laplace/external here
    # would silently re-break CI on the next run of this script.
    [LAPLACE_EXTERNAL]="/build/external"
    [LAPLACE_INSTALL_PREFIX]="/opt/laplace"
    [LAPLACE_PG_PREFIX]="/opt/laplace/pgsql-18"
    # One canonical HTTPS origin drives identity callbacks and Stripe returns.
    [LAPLACE_PUBLIC_BASE_URL]="${LAPLACE_PUBLIC_BASE_URL:-https://hart-server:8443}"
    # hart-server is the development deployment. Stripe remains fully active,
    # but execution is not paywalled unless a production deployment opts in.
    [LAPLACE_BILLING_BYPASS]="${LAPLACE_BILLING_BYPASS:-true}"
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
echo "Override per-run via 'gh workflow run laplace.yml' or repo Actions variables."
