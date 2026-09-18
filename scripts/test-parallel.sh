#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# shellcheck source=scripts/lib/storage.sh
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init

MODE=all
SUITE=
SERIAL="${LAPLACE_TEST_SERIAL:-0}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) [[ $# -ge 2 ]] || { echo "--profile needs a name" >&2; exit 2; }; MODE="$2"; shift 2 ;;
    --suite) [[ $# -ge 2 ]] || { echo "--suite needs a name" >&2; exit 2; }; SUITE="$2"; shift 2 ;;
    --engine) MODE=dev; shift ;;
    --regress|--app-db|--integration) MODE=db; shift ;;
    --app) MODE=app; shift ;;
    --app-dev) MODE=dev-managed; shift ;;
    --app-live) MODE=live; shift ;;
    --perf) MODE=perf; shift ;;
    --serial) SERIAL=1; shift ;;
    --all) shift ;;
    -h|--help)
      cat <<'EOF'
Usage: scripts/test-parallel.sh [--profile NAME] [--suite NAME] [--serial]
Profiles: dev-native, dev-managed, db, live, perf, dev, app, all.
Suites: native-dev, managed-dev, uci-dev, browser-dev, db-health, native-db,
        managed-db, live-floor, live-api, managed-live, generation-eval.
EOF
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ "$SERIAL" == 1 ]]; then
  export CTEST_PARALLEL_LEVEL=1
elif [[ -z "${CTEST_PARALLEL_LEVEL:-}" ]]; then
  CTEST_PARALLEL_LEVEL="$(nproc 2>/dev/null || echo 1)"
  export CTEST_PARALLEL_LEVEL
fi

sync_managed_native() {
  bash scripts/sync-managed-native-artifacts.sh
}

set_dev_perfcache() {
  local build_dir candidate
  # Pre-install qualification must execute the exact candidate ROM, never the
  # currently installed floor. The physical CMake tree lives on /build and the
  # checkout-local build symlink is not an authority for artifact selection.
  build_dir="$(python3 "$ROOT/scripts/place-build-directory.py" "$ROOT")"
  candidate=$(find -L "$build_dir" -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  [[ -n "$candidate" ]] || {
    echo "::error::candidate T0 perfcache missing under $build_dir — build native before managed qualification" >&2
    return 1
  }
  export LAPLACE_PERFCACHE_BIN="$candidate"
  export LAPLACE_ENGINE_BUILD="$build_dir/engine"
  echo "::notice::managed dev qualification T0 ROM: $LAPLACE_PERFCACHE_BIN"
}

set_installed_perfcache() {
  local candidate
  candidate=$(find "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/share/laplace" -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  if [[ -z "$candidate" ]]; then
    candidate=$(find -L build -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  fi
  [[ -z "$candidate" ]] || export LAPLACE_PERFCACHE_BIN="$candidate"
}

run_ctest() {
  python3 "$ROOT/scripts/provision-cmake.py" \
    --root "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/tools/cmake" \
    --work "${LAPLACE_WORK_ROOT:-/build/laplace/work}/cmake" \
    --exec-tool ctest -- "$@"
}

run_native_dev() {
  run_ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -LE regress
}

managed_test_solution() {
  local selected="$1" label="$2" work solution
  if [[ "$selected" == all ]]; then
    printf '%s\n' "$ROOT/app/Laplace.slnx"
    return 0
  fi
  [[ -n "$selected" ]] || return 3
  work="${LAPLACE_WORK_ROOT:-/build/laplace/work}/managed-solutions"
  mkdir -p "$work"
  solution="$(mktemp "$work/${label}.XXXXXX.slnx")"
  python3 "$ROOT/scripts/ci_managed_projects.py" --root "$ROOT" solution \
    --projects "$selected" --output "$solution"
  printf '%s\n' "$solution"
}

run_managed_dotnet_tests() {
  local selected="$1" label="$2" filter="$3"
  shift 3
  if [[ -z "$selected" ]]; then
    echo "::notice::managed impact plan selected no $label test projects"
    return 0
  fi

  local solution generated="" rc=0 deadline="${LAPLACE_MANAGED_TEST_TIMEOUT:-15m}"
  solution="$(managed_test_solution "$selected" "$label")" || return $?
  [[ "$solution" == "$ROOT/app/Laplace.slnx" ]] || generated="$solution"

  echo "::notice::$label projects=$selected deadline=$deadline"
  timeout --signal=TERM --kill-after=30s "$deadline" \
    dotnet test "$solution" -c Release --no-build --nologo --verbosity minimal \
      "$@" --filter "$filter" || rc=$?

  [[ -z "$generated" ]] || rm -f "$generated"
  if (( rc == 124 || rc == 137 )); then
    echo "::error::$label exceeded managed test deadline $deadline" >&2
  fi
  return "$rc"
}

run_managed_dev() {
  set_dev_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_TEST_PROJECTS:-all}" managed-dev \
    'Tier!=db&Tier!=live&Tier!=perf'
}

run_uci_dev() {
  local runtime output
  set_dev_perfcache
  sync_managed_native
  runtime=$(mktemp -d)
  trap 'rm -rf "$runtime"' RETURN
  dotnet publish app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj -c Release --no-build --no-self-contained -o "$runtime" --nologo
  output=$(printf 'uci\nisready\nquit\n' | timeout 30 "$runtime/laplace-uci")
  grep -q '^uciok$' <<<"$output"
  grep -q '^readyok$' <<<"$output"
  trap - RETURN
  rm -rf "$runtime"
}

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

run_db_health() {
  bash scripts/check-database-health.sh "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
}

run_native_db() {
  set_installed_perfcache
  run_ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -L regress
}

run_managed_db() {
  set_installed_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_DB_TEST_PROJECTS:-all}" managed-db \
    'Tier=db' -m:1 -p:BuildInParallel=false
}

run_live_floor() {
  set_installed_perfcache
  bash scripts/check-substrate-floor.sh "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
}

run_live_api() {
  local base capabilities readiness inventory completion code_completion code_chat
  base="${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}"
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
  if ! python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["atom_window"]==0x110000; assert d["perfcache_receipt_hex"]; assert d["database_perfcache_receipt_hex"]; assert not d.get("database_perfcache_error"); assert d["database_perfcache_receipt_hex"].lower()==d["perfcache_receipt_hex"].lower(); assert d["perfcache_aligned"] is True; assert all(len(n["hilbert_hex"])==32 for n in d["nodes"]); leaves=[n for n in d["nodes"] if n["tier"]==0]; assert leaves and all(n["ducet_rank"] is not None for n in leaves); r=next(n for n in d["nodes"] if n["ordinal"]==d["natural_unit_ordinal"]); assert r["packed_vertices"]; assert sum(v["run_length"] for v in r["packed_vertices"])==len(r["realized_vertices"])' <<<"$storage_proof"; then
    echo "::error::live storage proof does not demonstrate app/database ROM alignment and exact packed composition" >&2
    printf '%s\n' "$storage_proof" >&2
    return 1
  fi
  mkdir -p "$ROOT/build/eval-proof"
  (
    cd "$ROOT/web"
    npx playwright install chromium
    LAPLACE_UI_URL="$base" \
      LAPLACE_STORAGE_PROOF_EVIDENCE_DIR="$ROOT/build/eval-proof" \
      node scripts/verify-storage-proof-live.mjs
  ) || {
    echo "::error::rendered live Storage Proof verification failed" >&2
    return 1
  }
  proof_html=$(curl -fsS "$base/proof")
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

run_managed_live() {
  set_installed_perfcache
  sync_managed_native
  run_managed_dotnet_tests "${LAPLACE_MANAGED_LIVE_TEST_PROJECTS:-all}" managed-live 'Tier=live'
}

run_generation_eval() {
  mkdir -p "$ROOT/build/eval-proof"
  python3 scripts/eval-generation.py --api "${LAPLACE_API_BASE:-${LAPLACE_DEPLOYED_API_BASE:-http://127.0.0.1:5187}}" \
    --probes scripts/eval-probes.json --baseline scripts/eval-baselines.json --report "$ROOT/build/eval-proof/generation.json"
}

run_perf() {
  set_installed_perfcache
  sync_managed_native
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal --filter 'Tier=perf'
  mkdir -p "$ROOT/build/eval-proof"
  python3 scripts/verify-generation.py --api "${LAPLACE_API_BASE:-http://127.0.0.1:8080}" \
    --report "$ROOT/build/eval-proof/lane-detectors.json" --enforce
}

run_suite() {
  case "$1" in
    native-dev) run_native_dev ;;
    managed-dev) run_managed_dev ;;
    uci-dev) run_uci_dev ;;
    browser-dev) run_browser_dev ;;
    db-health) run_db_health ;;
    native-db) run_native_db ;;
    managed-db) run_managed_db ;;
    live-floor) run_live_floor ;;
    live-api) run_live_api ;;
    managed-live) run_managed_live ;;
    generation-eval) run_generation_eval ;;
    *) echo "unknown suite: $1" >&2; exit 2 ;;
  esac
}

if [[ -n "$SUITE" ]]; then
  run_suite "$SUITE"
  exit 0
fi

case "$MODE" in
  dev-native) run_native_dev ;;
  dev-managed) run_managed_dev; run_uci_dev; run_browser_dev ;;
  dev) run_native_dev; run_managed_dev; run_uci_dev; run_browser_dev ;;
  db) run_db_health; run_native_db; run_managed_db ;;
  live) run_live_floor; run_live_api; run_managed_live; run_generation_eval ;;
  perf) run_perf ;;
  app) run_managed_dev; run_uci_dev; run_browser_dev; run_db_health; run_native_db; run_managed_db ;;
  all) run_native_dev; run_managed_dev; run_uci_dev; run_browser_dev; run_db_health; run_native_db; run_managed_db ;;
  *) echo "unknown profile: $MODE" >&2; exit 2 ;;
esac
