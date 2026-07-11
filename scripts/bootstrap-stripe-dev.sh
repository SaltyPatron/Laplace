#!/bin/bash

set -euo pipefail

ENV_DIR="${HOME}/.config/laplace"
ENV_FILE="${ENV_DIR}/stripe-dev.env"
DEFAULT_SUCCESS_URL="http://127.0.0.1:5187/billing/success"
DEFAULT_CANCEL_URL="http://127.0.0.1:5187/billing/cancel"
DEFAULT_CURRENCY="usd"
PERSIST_ZSH=0
PRINT_ONLY=0
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

if ! command -v stripe >/dev/null 2>&1; then
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

echo "Wrote ${ENV_FILE} (mode 600)."
echo "Load now: source ${ENV_FILE}"

echo
echo "Optional Stripe CLI checks:"
echo "  stripe whoami"
echo "  stripe listen --forward-to http://127.0.0.1:5187/v1/billing/webhooks/stripe --device-name laplace-dev"

auto_line="source ${ENV_FILE}"
if [[ "${PERSIST_ZSH}" -eq 1 ]]; then
  ZSHRC="${HOME}/.zshrc"
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
