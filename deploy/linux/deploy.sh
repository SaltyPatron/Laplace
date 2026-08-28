#!/usr/bin/env bash
# Publish API + SPA + UCI and immutable MCP/Lichess runtimes to /opt/laplace/app.
#
# Options:
#   --force-npm    always run npm ci (ignore lockfile stamp)
#   --serial       publish API, UCI, MCP, Lichess serially (default: parallel)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
source "$REPO_ROOT/deploy/linux/app-dir-contract.sh"
source "$REPO_ROOT/deploy/linux/payload-sync.sh"
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

laplace_reconcile_app_dir_contract "$APP_DIR"

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
MCP_STAGE="$(mktemp -d)"
LICHESS_STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE" "$UCI_STAGE" "$MCP_STAGE" "$LICHESS_STAGE"' EXIT

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

publish_mcp() {
  echo "==> publish laplace-mcp -> $MCP_STAGE"
  dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj" \
    -c Release --no-self-contained -o "$MCP_STAGE"
}

publish_lichess() {
  dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj" \
    -c Release --no-self-contained -o "$LICHESS_STAGE"
}

if [[ "$SERIAL" -eq 1 ]]; then
  echo "==> [2/4] publish API + UCI + MCP + Lichess (serial)"
  publish_api
  publish_uci
  publish_mcp
  publish_lichess
else
  echo "==> [2/4] publish API || UCI || MCP || Lichess (parallel)"
  api_log="$(mktemp)"; uci_log="$(mktemp)"; mcp_log="$(mktemp)"; lichess_log="$(mktemp)"
  set +e
  publish_api >"$api_log" 2>&1 &
  api_pid=$!
  publish_uci >"$uci_log" 2>&1 &
  uci_pid=$!
  publish_mcp >"$mcp_log" 2>&1 &
  mcp_pid=$!
  publish_lichess >"$lichess_log" 2>&1 &
  lichess_pid=$!
  wait "$api_pid"; api_rc=$?
  wait "$uci_pid"; uci_rc=$?
  wait "$mcp_pid"; mcp_rc=$?
  wait "$lichess_pid"; lichess_rc=$?
  set -e
  cat "$api_log" "$uci_log" "$mcp_log" "$lichess_log"
  rm -f "$api_log" "$uci_log" "$mcp_log" "$lichess_log"
  if [[ "$api_rc" -ne 0 || "$uci_rc" -ne 0 || "$mcp_rc" -ne 0 || "$lichess_rc" -ne 0 ]]; then
    echo "::error::publish failed (api=$api_rc uci=$uci_rc mcp=$mcp_rc lichess=$lichess_rc)"
    exit 1
  fi
fi

echo "==> [3/4] overlay SPA; prepare isolated UCI/MCP/Lichess runtimes"
rm -rf "$STAGE/wwwroot"
mkdir -p "$STAGE/wwwroot"
cp -r "$REPO_ROOT/web/dist/." "$STAGE/wwwroot/"
test -x "$UCI_STAGE/laplace-uci"
test -f "$MCP_STAGE/Laplace.Endpoints.Mcp"
test -f "$LICHESS_STAGE/Laplace.Endpoints.Lichess"
chmod 0755 "$MCP_STAGE/Laplace.Endpoints.Mcp" "$LICHESS_STAGE/Laplace.Endpoints.Lichess"
# Retain the original mcp-runtime directory AND prior releases: running STDIO
# clients resolve managed/native dependencies relative to their original apphost.
# No rsync is ever allowed to overwrite those directories on a later publish.
release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"
release_name="$(basename "$release")"
ln -s "releases/$release_name/uci/laplace-uci" "$STAGE/laplace-uci"
ln -s "releases/$release_name/mcp/Laplace.Endpoints.Mcp" "$STAGE/laplace-mcp"
ln -s "releases/$release_name/lichess/Laplace.Endpoints.Lichess" "$STAGE/laplace-lichess"
mkdir "$STAGE/managed-services"
cp "$REPO_ROOT/deploy/linux/managed-services/"*.service "$STAGE/managed-services/"
cp "$REPO_ROOT/deploy/linux/laplace-managed-deploy" "$STAGE/managed-services/"

# Exercise the copied runtime before stopping/replacing the API. File existence
# alone accepted an apphost whose managed assembly was entirely absent.
python3 "$REPO_ROOT/scripts/check-uci-runtime.py" "$release/uci/laplace-uci"

echo "==> [4/4] sync isolated MCP runtime + app into $APP_DIR"
if [[ "${LAPLACE_MANAGED_TRANSACTION:-}" == "1" ]]; then
  sudo -n systemctl stop laplace-api
fi
laplace_sync_payload "$STAGE" "$APP_DIR" \
  --exclude 'laplace-api.env' --exclude 'agents.json' --exclude 'logs/' --exclude 'chess-lab-work/' \
  --exclude 'mcp-runtime/' --exclude 'mcp/' --exclude 'releases/'
laplace_require_app_dir_contract "$APP_DIR"
test -x "$APP_DIR/laplace-uci" || { echo "::error::laplace-uci missing from $APP_DIR after sync"; exit 1; }
test -x "$APP_DIR/laplace-mcp" || { echo "::error::laplace-mcp missing from $APP_DIR after sync"; exit 1; }
test -f "$release/mcp/Laplace.Endpoints.Mcp.dll"
test -x "$APP_DIR/laplace-lichess"
timeout 10s "$APP_DIR/laplace-mcp" </dev/null || { echo "::error::deployed laplace-mcp failed its EOF startup smoke test"; exit 1; }
echo "✓ published API + SPA + UCI + versioned MCP/Lichess to $APP_DIR"
