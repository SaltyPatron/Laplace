#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_live_api() {
  local base ui_base capabilities readiness inventory completion code_completion code_chat
  base="${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}"
  ui_base="${LAPLACE_PUBLIC_UI_BASE:-http://127.0.0.1:8080}"
  capabilities=$(curl -fsS "$base/v1/capabilities")
  grep -q '"chat_completions"' <<<"$capabilities"
  grep -q '"op"' <<<"$capabilities"
  readiness=$(curl -fsS "$base/health/ready")
  grep -q '"ready":true' <<<"$readiness"
  grep -q '"substrate_reachable":true' <<<"$readiness"
  inventory=$(curl -fsS -X POST "$base/v1/op" -H 'Content-Type: application/json' -H 'X-Laplace-Tenant: ci' \
    --data '{"name":"ops.substrate_counts","max_rows":20}')
  grep -q '"object":"op.result"' <<<"$inventory"
  storage_proof=$(curl -fsS -X POST "$base/v1/explore/storage-proof" \
    -H 'Content-Type: application/json' -H 'X-Laplace-Tenant: ci' \
    --data '{"text":"aa"}')
  if ! python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["atom_window"]==0x110000; assert d["perfcache_receipt_hex"]; assert d["database_perfcache_receipt_hex"]; assert not d.get("database_perfcache_error"); assert d["database_perfcache_receipt_hex"].lower()==d["perfcache_receipt_hex"].lower(); assert d["perfcache_aligned"] is True; assert all(len(n["hilbert_hex"])==32 for n in d["nodes"]); leaves=[n for n in d["nodes"] if n["tier"]==0]; assert leaves and all(n["ducet_rank"] is not None for n in leaves); r=next(n for n in d["nodes"] if n["id_hex"]==d["root_id_hex"]); assert r["packed_vertices"]; assert sum(v["run_length"] for v in r["packed_vertices"])==len(r["realized_vertices"])' <<<"$storage_proof"; then
    echo "::error::live storage proof does not demonstrate app/database ROM alignment and exact packed composition" >&2
    printf '%s\n' "$storage_proof" >&2
    return 1
  fi
  mkdir -p "$ROOT/build/eval-proof"
  (
    cd "$ROOT/web"
    npx playwright install chromium
    LAPLACE_API_BASE="$base" \
      LAPLACE_UI_URL="$ui_base" \
      LAPLACE_STORAGE_PROOF_EVIDENCE_DIR="$ROOT/build/eval-proof" \
      node scripts/verify-storage-proof-live.mjs
  ) || {
    echo "::error::rendered live Storage Proof verification failed" >&2
    return 1
  }
  proof_html=$(curl -fsS "$ui_base/proof?q=aa")
  if ! grep -q '<div id="root"' <<<"$proof_html"; then
    echo "::error::deployed application does not serve the Storage Proof SPA route at /proof" >&2
    return 1
  fi
  completion=$(curl -fsS -X POST "$base/v1/chat/completions" -H 'Content-Type: application/json' -H 'X-Laplace-Tenant: ci' \
    --data '{"model":"laplace-converse-001","messages":[{"role":"user","content":"dog"}]}')
  grep -q '"object":"chat.completion"' <<<"$completion"
  if ! python3 -c 'import json,sys; d=json.load(sys.stdin); c=d["choices"][0]["message"]["content"]; p=d["metadata"]["performance"]; assert isinstance(c,str) and c.strip(); assert p["output_words"] > 0' <<<"$completion"; then
    echo "::error::live chat returned a completion envelope without realized text" >&2
    printf '%s\n' "$completion" >&2
    return 1
  fi

  code_completion=$(curl -fsS -X POST "$base/v1/code/completions" \
    -H 'Content-Type: application/json' -H 'X-Laplace-Tenant: ci' \
    --data '{"model":"laplace-code-001","prompt":"main","code_language":"python","max_tokens":256,"window":8,"temperature":0.2,"top_k":32,"max_attempts":6}')
  if ! python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["object"]=="code.completion"; assert d["model"]=="laplace-code-001"; assert d["verified"] is True; assert isinstance(d["code"],str) and d["code"].strip(); assert d["candidate_id"]; a=d["attempts"]; assert a and a[-1]["verified"] is True' <<<"$code_completion"; then
    echo "::error::live code player did not close generation -> AST admission -> toolchain witness -> fold" >&2
    printf '%s\n' "$code_completion" >&2
    return 1
  fi

  code_chat=$(curl -fsS -X POST "$base/v1/chat/completions" \
    -H 'Content-Type: application/json' -H 'X-Laplace-Tenant: ci' \
    --data '{"model":"laplace-code-001","messages":[{"role":"user","content":"main"}],"code_language":"python","max_tokens":256,"window":8,"temperature":0.2,"top_k":32,"max_attempts":6}')
  if ! python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["object"]=="chat.completion"; assert d["model"]=="laplace-code-001"; c=d["choices"][0]["message"]["content"]; assert isinstance(c,str) and c.strip()' <<<"$code_chat"; then
    echo "::error::laplace-code-001 did not own the OpenAI chat route" >&2
    printf '%s\n' "$code_chat" >&2
    return 1
  fi
}

run_live_api
