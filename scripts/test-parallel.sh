#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

MODE=all
SUITE=

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
    --serial) export LAPLACE_TEST_SERIAL=1; shift ;;
    --all) shift ;;
    -h|--help)
      cat <<'EOF'
Usage: scripts/test-parallel.sh [--profile NAME] [--suite NAME] [--serial]
Profiles: dev-native, dev-managed, db, live, perf, dev, app, all.
Suites: native-dev, managed-dev, uci-dev, browser-dev, db-health, native-db,
        managed-db, live-floor, live-api, managed-live, generation-eval,
        chess-provider-live.
EOF
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

run_suite() {
  local suite="$1" driver="$ROOT/scripts/test-suites/$1.sh"
  case "$suite" in
    native-dev|managed-dev|uci-dev|browser-dev|db-health|native-db|managed-db|live-floor|live-api|managed-live|generation-eval|chess-provider-live|perf) ;;
    *) echo "unknown suite: $suite" >&2; exit 2 ;;
  esac
  [[ -f "$driver" ]] || { echo "missing suite driver: $driver" >&2; exit 2; }
  bash "$driver"
}

if [[ -n "$SUITE" ]]; then
  run_suite "$SUITE"
  exit 0
fi

case "$MODE" in
  dev-native) run_suite native-dev ;;
  dev-managed) run_suite managed-dev; run_suite uci-dev; run_suite browser-dev ;;
  dev) run_suite native-dev; run_suite managed-dev; run_suite uci-dev; run_suite browser-dev ;;
  db) run_suite db-health; run_suite native-db; run_suite managed-db ;;
  live) run_suite live-floor; run_suite live-api; run_suite managed-live; run_suite generation-eval; run_suite chess-provider-live ;;
  perf) run_suite perf ;;
  app) run_suite managed-dev; run_suite uci-dev; run_suite browser-dev; run_suite db-health; run_suite native-db; run_suite managed-db ;;
  all) run_suite native-dev; run_suite managed-dev; run_suite uci-dev; run_suite browser-dev; run_suite db-health; run_suite native-db; run_suite managed-db ;;
  *) echo "unknown profile: $MODE" >&2; exit 2 ;;
esac
