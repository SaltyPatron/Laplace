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
  export CTEST_PARALLEL_LEVEL="$(nproc 2>/dev/null || echo 1)"
fi

bash scripts/sync-managed-native-artifacts.sh

set_dev_perfcache() {
  local candidate
  candidate=$(find -L build -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  [[ -z "$candidate" ]] || export LAPLACE_PERFCACHE_BIN="$candidate"
}

set_installed_perfcache() {
  local candidate
  candidate=$(find "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/share/laplace" -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  if [[ -z "$candidate" ]]; then
    candidate=$(find -L build -type f -name 'laplace_t0_perfcache*.bin' -print 2>/dev/null | sort | tail -1 || true)
  fi
  [[ -z "$candidate" ]] || export LAPLACE_PERFCACHE_BIN="$candidate"
}

run_native_dev() {
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -LE regress
}

run_managed_dev() {
  set_dev_perfcache
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal \
    --filter 'Tier!=db&Tier!=live&Tier!=perf'
}

run_uci_dev() {
  local runtime output
  runtime=$(mktemp -d)
  trap 'rm -rf "$runtime"' RETURN
  dotnet publish app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj -c Release --no-self-contained -o "$runtime" --nologo
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
  ctest --test-dir build --output-on-failure -j "$CTEST_PARALLEL_LEVEL" -L regress
}

run_managed_db() {
  set_installed_perfcache
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal --filter 'Tier=db'
}

run_live_floor() {
  set_installed_perfcache
  bash scripts/check-substrate-floor.sh "${LAPLACE_DBNAME:-${PGDATABASE:-laplace}}"
}

run_live_api() {
  local base capabilities readiness inventory completion
  base="${LAPLACE_API_BASE:-http://127.0.0.1:8080}"
  capabilities=$(curl -fsS "$base/v1/capabilities")
  grep -q '"chat_completions"' <<<"$capabilities"
  grep -q '"op"' <<<"$capabilities"
  readiness=$(curl -fsS "$base/health/ready")
  grep -q '"ready":true' <<<"$readiness"
  grep -q '"substrate_reachable":true' <<<"$readiness"
  inventory=$(curl -fsS -X POST "$base/v1/op" -H 'Content-Type: application/json' -H 'X-Laplace-Tenant: ci' \
    --data '{"name":"ops.substrate_counts","max_rows":20}')
  grep -q '"object":"op.result"' <<<"$inventory"
  completion=$(curl -fsS -X POST "$base/v1/chat/completions" -H 'Content-Type: application/json' -H 'X-Laplace-Tenant: ci' \
    --data '{"model":"laplace-converse-001","messages":[{"role":"user","content":"dog"}]}')
  grep -q '"object":"chat.completion"' <<<"$completion"
}

run_managed_live() {
  set_installed_perfcache
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal --filter 'Tier=live'
}

run_generation_eval() {
  mkdir -p .eval-proof
  python3 scripts/eval-generation.py --api "${LAPLACE_API_BASE:-http://127.0.0.1:8080}" \
    --probes scripts/eval-probes.json --baseline scripts/eval-baselines.json --report .eval-proof/generation.json
}

run_perf() {
  set_installed_perfcache
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal --filter 'Tier=perf'
  mkdir -p .eval-proof
  python3 scripts/verify-generation.py --api "${LAPLACE_API_BASE:-http://127.0.0.1:8080}" \
    --report .eval-proof/lane-detectors.json --enforce
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
