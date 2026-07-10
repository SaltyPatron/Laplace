#!/usr/bin/env bash
# Publish API + SPA + laplace-uci to /opt/laplace/app.
#
# Options:
#   --force-npm    always run npm ci (ignore lockfile stamp)
#   --serial       publish API then UCI serially (default: parallel)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
STAGE="$(mktemp -d)"
FORCE_NPM=0
SERIAL=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force-npm) FORCE_NPM=1; shift ;;
    --serial)    SERIAL=1; shift ;;
    -h|--help)
      sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

trap 'rm -rf "$STAGE"' EXIT

echo "==> [1/4] build front-end (web/ -> dist)"
pushd "$REPO_ROOT/web" >/dev/null
stamp="node_modules/.laplace-npm-ci.stamp"
need_ci=1
if [[ "$FORCE_NPM" -eq 0 && -d node_modules && -f package-lock.json && -f "$stamp" ]]; then
  lock_hash=$(sha256sum package-lock.json | awk '{print $1}')
  prev=$(cat "$stamp" 2>/dev/null || true)
  if [[ "$prev" == "$lock_hash" ]]; then
    echo "    npm ci skipped (package-lock stamp fresh; pass --force-npm to override)"
    need_ci=0
  fi
fi
if [[ "$need_ci" -eq 1 ]]; then
  npm ci --no-audit --no-fund
  mkdir -p node_modules
  sha256sum package-lock.json | awk '{print $1}' > "$stamp"
fi
test -f openapi/openapi.json || { echo "::error::web/openapi/openapi.json missing — run pipeline.sh build first"; exit 1; }
echo "    generating src/api/types.gen.ts from openapi/openapi.json"
npm run gen:api
npm run build
popd >/dev/null

UCI_STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE" "$UCI_STAGE"' EXIT

publish_api() {
  echo "==> publish API -> staging ($STAGE)"
  dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj" \
    -c Release --no-self-contained -o "$STAGE"
}

publish_uci() {
  echo "==> publish laplace-uci -> $UCI_STAGE"
  dotnet publish "$REPO_ROOT/app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj" \
    -c Release --no-self-contained -o "$UCI_STAGE"
}

if [[ "$SERIAL" -eq 1 ]]; then
  echo "==> [2/4] publish API + UCI (serial)"
  publish_api
  publish_uci
else
  echo "==> [2/4] publish API || UCI (parallel)"
  api_log="$(mktemp)"; uci_log="$(mktemp)"
  set +e
  publish_api >"$api_log" 2>&1 &
  api_pid=$!
  publish_uci >"$uci_log" 2>&1 &
  uci_pid=$!
  wait "$api_pid"; api_rc=$?
  wait "$uci_pid"; uci_rc=$?
  set -e
  cat "$api_log" "$uci_log"
  rm -f "$api_log" "$uci_log"
  if [[ "$api_rc" -ne 0 || "$uci_rc" -ne 0 ]]; then
    echo "::error::publish failed (api_rc=$api_rc uci_rc=$uci_rc)"
    exit 1
  fi
fi

echo "==> [3/4] overlay SPA + UCI into staging"
rm -rf "$STAGE/wwwroot"
mkdir -p "$STAGE/wwwroot"
cp -r "$REPO_ROOT/web/dist/." "$STAGE/wwwroot/"
if [[ -f "$UCI_STAGE/laplace-uci" ]]; then
  cp -f "$UCI_STAGE/laplace-uci" "$STAGE/laplace-uci"
  chmod 0755 "$STAGE/laplace-uci"
elif [[ -f "$UCI_STAGE/laplace-uci.exe" ]]; then
  cp -f "$UCI_STAGE/laplace-uci.exe" "$STAGE/laplace-uci"
  chmod 0755 "$STAGE/laplace-uci"
else
  echo "::error::laplace-uci missing from UCI publish output"
  ls -la "$UCI_STAGE" || true
  exit 1
fi

echo "==> [4/4] sync into $APP_DIR (preserve env + logs)"
rsync -a --delete \
  --exclude 'laplace-api.env' --exclude 'logs/' --exclude 'chess-lab-work/' \
  "$STAGE/" "$APP_DIR/"
test -x "$APP_DIR/laplace-uci" || { echo "::error::laplace-uci missing from $APP_DIR after sync"; exit 1; }
echo "✓ published API + SPA + laplace-uci to $APP_DIR"
