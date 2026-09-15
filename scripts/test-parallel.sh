#!/usr/bin/env bash
# Compatibility aliases for the single executable test-profile authority.
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
    --profile)    [[ $# -ge 2 ]] || { echo "--profile needs a name" >&2; exit 2; }; MODE="$2"; shift 2 ;;
    --suite)      [[ $# -ge 2 && -z "$SUITE" && -n "$2" ]] || { echo "--suite needs exactly one name" >&2; exit 2; }; SUITE="$2"; shift 2 ;;
    --engine)      MODE=dev; shift ;;
    --regress)     MODE=db; shift ;;
    --app)         MODE=app; shift ;;
    --app-dev)     MODE=dev-managed; shift ;;
    --app-db)      MODE=db; shift ;;
    --app-live)    MODE=live; shift ;;
    --integration) MODE=db; shift ;;
    --perf)        MODE=perf; shift ;;
    --policy)      MODE=policy; shift ;;
    --serial)      SERIAL=1; shift ;;
    --all)         shift ;; # Required profiles never fingerprint-skip.
    -h|--help)
      cat <<'EOF'
Usage: scripts/test-parallel.sh [profile alias] [--serial]
  --profile NAME select one canonical registry profile
  --suite ID     select exactly one registered suite from that profile
  --engine       DEV/BAT: native + managed + UCI + browser
  --regress      database QA (health + pg_regress + managed DB fixtures)
  --app          managed DEV/BAT followed by database QA
  --app-dev      managed DEV/BAT
  --app-db       database QA
  --app-live     seeded/shared product acceptance
  --integration database QA
  --perf         explicit performance profile
  --policy       deterministic source/policy profile
  --serial       force CTest parallelism to one
EOF
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ "$SERIAL" == 1 ]]; then
  export CTEST_PARALLEL_LEVEL=1
elif [[ -z "${CTEST_PARALLEL_LEVEL:-}" ]]; then
  nproc_n="$(nproc 2>/dev/null || echo 1)"
  export CTEST_PARALLEL_LEVEL="$nproc_n"
fi

# Managed processes load app-local native libraries before the OS loader path. A native-only
# source change can therefore leave stale .so files beside already-built .NET outputs even
# when build/engine contains the exact new artifact. Synchronize that closure before any
# profile that can execute managed/native code. Policy is source-only and needs no build.
if [[ "$MODE" != policy ]]; then
  bash scripts/sync-managed-native-artifacts.sh
fi

run_profile() {
  local args=()
  [[ -z "$SUITE" ]] || args+=(--suite "$SUITE")
  python3 scripts/test-profile-registry.py run --profile "$1" "${args[@]}"
}

if [[ "$MODE" == app && -n "$SUITE" ]]; then
  echo "--suite requires one registry profile; use --profile" >&2
  exit 2
fi

if [[ "$MODE" == app ]]; then
  run_profile dev-managed
  run_profile db
elif [[ "$MODE" == live ]]; then
  # Keep the registry as the one executable test authority. If it fails, collect
  # a read-only endpoint receipt only after the authoritative verdict is already
  # red, then propagate the same failure. This turns an opaque chained-curl 503
  # into endpoint-specific evidence without adding a second success criterion.
  if ! run_profile live; then
    bash scripts/diagnose-live-endpoints.sh || true
    exit 1
  fi
else
  run_profile "$MODE"
fi
