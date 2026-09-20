#!/bin/bash

set -euo pipefail

# THE OPERATOR'S home, not root's. setup-host.sh runs this under sudo, where $HOME
# is /root — so this wrote /root/.config/laplace/stripe-dev.env and appended the
# source line to /root/.zshrc, while the operator's own config sat untouched and the
# key never reached the shell that needed it (2026-08-12).
#
# setup-host.sh already resolves this ONCE, at the only point where SUDO_USER is
# still the human, and exports it as LAPLACE_OPERATOR — the same nested-sudo problem
# its own comment documents for the bootstrap. Honour it here; fall back to
# SUDO_USER, then to $HOME for a non-sudo invocation.
_op="${LAPLACE_OPERATOR:-${SUDO_USER:-}}"
if [ -n "$_op" ] && [ "$_op" != root ]; then
    OP_HOME="$(getent passwd "$_op" | cut -d: -f6)"
else
    OP_HOME="$HOME"
fi
[ -n "$OP_HOME" ] || OP_HOME="$HOME"
ENV_DIR="${OP_HOME}/.config/laplace"
ENV_FILE="${ENV_DIR}/stripe-dev.env"
DEFAULT_SUCCESS_URL="http://127.0.0.1:5187/billing/success"
DEFAULT_CANCEL_URL="http://127.0.0.1:5187/billing/cancel"
DEFAULT_CURRENCY="usd"
PERSIST_ZSH=0
PRINT_ONLY=0
INSTALL_SERVICE=0
# Prefer operator name; accept legacy LAPLACE_STRIPE_API_KEY.
API_KEY="${STRIPE_API_SECRET:-${LAPLACE_STRIPE_API_KEY:-}}"
WEBHOOK_SECRET="${STRIPE_WEBHOOK_SECRET:-${LAPLACE_STRIPE_WEBHOOK_SECRET:-}}"

usage() {
  cat <<'EOF'
Usage: scripts/bootstrap-stripe-dev.sh [options]

Options:
  --api-key <value>   Stripe test secret (sk_test_...) → STRIPE_API_SECRET
  --persist-zsh       Append source line to ~/.zshrc if missing
  --print-only        Print export lines only, do not write files
  --install-service   Install/start the Linux Stripe webhook listener (root)
  -h, --help          Show this help

Recommended flow:
  1) Put STRIPE_API_SECRET=sk_test_... in ~/.config/shell/secrets.env (and repo .env on Windows)
  2) Or run this script and enter sk_test_... when prompted
  3) source ~/.config/laplace/stripe-dev.env
  4) On Windows: scripts\win\install-stripe-listen.cmd (NSSM) for webhook forwarding
  5) Call /v1/billing/catalog/sync then preflight with LAPLACE_BILLING_BYPASS=false
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api-key)
      API_KEY="${2:-}"
      shift 2
      ;;
    --persist-zsh)
      PERSIST_ZSH=1
      shift
      ;;
    --print-only)
      PRINT_ONLY=1
      shift
      ;;
    --install-service)
      INSTALL_SERVICE=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 2
      ;;
  esac
done

STRIPE_CLI="$(command -v stripe 2>/dev/null || true)"
if [[ -z "$STRIPE_CLI" && -x "$OP_HOME/.npm-global/bin/stripe" ]]; then
  STRIPE_CLI="$OP_HOME/.npm-global/bin/stripe"
fi
if [[ -z "$STRIPE_CLI" ]]; then
  echo "stripe CLI not found. Install: https://docs.stripe.com/stripe-cli/install" >&2
  echo "Continuing without CLI; key/bootstrap still works." >&2
fi

if [[ -z "${API_KEY}" ]]; then
  read -r -s -p "Enter Stripe test key (sk_test_...): " API_KEY
  echo
fi

if [[ -z "${API_KEY}" ]]; then
  echo "No key provided. Aborting." >&2
  exit 1
fi

if [[ "${API_KEY}" != sk_test_* && "${API_KEY}" != rk_test_* ]]; then
  echo "Warning: key does not look like a Stripe test restricted/secret key." >&2
fi

ENV_CONTENT=$(cat <<EOF
export STRIPE_API_SECRET='${API_KEY}'
export LAPLACE_STRIPE_SUCCESS_URL='${DEFAULT_SUCCESS_URL}'
export LAPLACE_STRIPE_CANCEL_URL='${DEFAULT_CANCEL_URL}'
export LAPLACE_BILLING_CURRENCY='${DEFAULT_CURRENCY}'
EOF
)
if [[ -n "${WEBHOOK_SECRET}" ]]; then
  ENV_CONTENT="${ENV_CONTENT}
export STRIPE_WEBHOOK_SECRET='${WEBHOOK_SECRET}'"
fi

if [[ "${PRINT_ONLY}" -eq 1 ]]; then
  printf "%s\n" "${ENV_CONTENT}"
  exit 0
fi

mkdir -p "${ENV_DIR}"
umask 077
printf "%s\n" "${ENV_CONTENT}" > "${ENV_FILE}"
chmod 600 "${ENV_FILE}"
# Under sudo these are created by ROOT inside the operator's home, and mode 600
# then makes them unreadable by the very account whose shell sources them. Hand
# them back. No-op when not running as root.
if [ "$(id -u)" -eq 0 ] && [ -n "${_op:-}" ] && [ "$_op" != root ]; then
  chown "$_op:$(id -gn "$_op")" "${ENV_DIR}" "${ENV_FILE}"
fi

echo "Wrote ${ENV_FILE} (mode 600)."
echo "Load now: source ${ENV_FILE}"

echo
echo "Optional Stripe CLI checks:"
echo "  stripe whoami"
echo "  stripe listen --forward-to http://127.0.0.1:5187/v1/billing/webhooks/stripe --device-name laplace-dev"

auto_line="source ${ENV_FILE}"
if [[ "${PERSIST_ZSH}" -eq 1 ]]; then
  ZSHRC="${OP_HOME}/.zshrc"
  touch "${ZSHRC}"
  if ! grep -Fq "${auto_line}" "${ZSHRC}"; then
    printf "\n%s\n" "${auto_line}" >> "${ZSHRC}"
    echo "Added source line to ${ZSHRC}."
  else
    echo "${ZSHRC} already sources ${ENV_FILE}."
  fi
fi

echo
echo "How to set key in CI/runner environment:"
echo "  1) Store STRIPE_API_SECRET in ~/.config/shell/secrets.env (setup-host seeds /opt/laplace/secrets/stripe.env)."
echo "  2) Or export STRIPE_API_SECRET before pipeline publish (refreshes the drop)."
echo "  3) Keep live keys separate; never reuse test keys in live mode."

install_listener_service() {
  if [[ "$(id -u)" -ne 0 ]]; then
    echo "--install-service requires root (use setup-host.sh stripe)." >&2
    return 1
  fi
  if [[ -z "$STRIPE_CLI" ]]; then
    echo "Stripe CLI not found for operator $OP_HOME; listener service not installed." >&2
    return 1
  fi

  local native_cli="$STRIPE_CLI"
  local resolved
  resolved="$(readlink -f "$STRIPE_CLI")"
  if [[ "$resolved" == */@stripe/cli/bin/shim.js ]]; then
    local candidates=("$OP_HOME"/.npm-global/lib/node_modules/@stripe/cli/node_modules/@stripe/cli-linux-*/bin/stripe)
    if [[ ${#candidates[@]} -ne 1 || ! -x "${candidates[0]}" ]]; then
      echo "Could not resolve the native Stripe CLI behind $STRIPE_CLI." >&2
      return 1
    fi
    native_cli="${candidates[0]}"
  fi
  "$native_cli" version >/dev/null

  install -d -o laplace-runner -g laplace-runner -m 2775 /opt/laplace/bin
  install -o root -g laplace-runner -m 0755 "$native_cli" /opt/laplace/bin/stripe

  local secret_file=/opt/laplace/secrets/stripe.env
  install -d -o laplace-runner -g laplace-runner -m 2770 /opt/laplace/secrets
  local temp
  temp="$(mktemp /opt/laplace/secrets/.stripe.env.XXXXXX)"
  if [[ -f "$secret_file" ]]; then
    grep -v '^STRIPE_API_KEY=' "$secret_file" >"$temp" || true
  fi
  printf 'STRIPE_API_KEY=%s\n' "$API_KEY" >>"$temp"
  chown laplace-runner:laplace-runner "$temp"
  chmod 0640 "$temp"
  mv -f "$temp" "$secret_file"

  local unit_src
  unit_src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/deploy/linux/managed-services/laplace-stripe.service"
  install -o root -g root -m 0644 "$unit_src" /etc/systemd/system/laplace-stripe.service
  systemctl daemon-reload
  systemctl enable --now laplace-stripe.service
  systemctl is-active --quiet laplace-stripe.service
  echo "Stripe listener active: laplace-stripe.service -> http://127.0.0.1:5187/v1/billing/webhooks/stripe"
}

if [[ "$INSTALL_SERVICE" -eq 1 ]]; then
  install_listener_service
fi
