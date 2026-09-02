#!/usr/bin/env bash
# Failure-only receipt for the seeded live product profile.
#
# The live suite used one &&-chained curl command, so HTTP 503 named neither the
# endpoint nor the readiness/op/chat body that explained it. This script changes
# no product state and does not turn any failure green. It is invoked only after
# the authoritative live profile has already failed and records each public
# product check independently.
set -u

base="${LAPLACE_API_BASE:-http://127.0.0.1:8080}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

probe() {
  local name="$1"
  shift
  local body="$tmp/$name.body"
  local headers="$tmp/$name.headers"
  local status rc

  set +e
  status="$(curl -sS -D "$headers" -o "$body" -w '%{http_code}' "$@")"
  rc=$?
  set -e

  printf '\n===== LIVE RECEIPT: %s =====\n' "$name"
  printf 'request=%s\n' "$*"
  printf 'curl_rc=%s http_status=%s\n' "$rc" "${status:-unavailable}"
  if [[ -s "$headers" ]]; then
    # Status/content type/request ids are useful; do not dump authorization or
    # other request-side secrets (curl -D records response headers only).
    grep -Ei '^(HTTP/|content-type:|retry-after:|x-request-id:|traceparent:)' "$headers" || true
  fi
  printf '%s\n' '--- body ---'
  if [[ -s "$body" ]]; then
    cat "$body"
    printf '\n'
  else
    printf '<empty>\n'
  fi
}

set -e
probe capabilities \
  "$base/v1/capabilities"
probe readiness \
  "$base/health/ready"
probe op-substrate-counts \
  -X POST "$base/v1/op" \
  -H 'Content-Type: application/json' \
  -H 'X-Laplace-Tenant: ci' \
  --data '{"name":"ops.substrate_counts","max_rows":20}'
probe chat-dog \
  -X POST "$base/v1/chat/completions" \
  -H 'Content-Type: application/json' \
  -H 'X-Laplace-Tenant: ci' \
  --data '{"model":"laplace-converse-001","messages":[{"role":"user","content":"dog"}]}'

# Diagnostic only. The caller retains the original live-profile failure code.
exit 0
