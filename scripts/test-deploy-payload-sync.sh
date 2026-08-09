#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/deploy/linux/payload-sync.sh"

TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

STAGE="$TEST_ROOT/stage"
APP_DIR="$TEST_ROOT/app"
MCP_STAGE="$TEST_ROOT/mcp-stage"
MCP_DIR="$APP_DIR/mcp-runtime"
mkdir -m 0700 "$STAGE"
mkdir -m 2775 "$APP_DIR"
mkdir -m 2775 "$APP_DIR/logs"
mkdir -m 0700 "$MCP_STAGE"
mkdir -m 2775 "$MCP_DIR"
APP_METADATA="$(stat -c '%u:%g:%a' "$APP_DIR")"
MCP_METADATA="$(stat -c '%u:%g:%a' "$MCP_DIR")"
printf 'keep\n' > "$APP_DIR/laplace-api.env"
printf 'keep\n' > "$APP_DIR/logs/app.csv"
printf 'remove\n' > "$APP_DIR/stale.dll"

mkdir "$STAGE/wwwroot"
printf 'first\n' > "$STAGE/api.dll"
printf 'first\n' > "$STAGE/wwwroot/index.html"
printf '#!/usr/bin/env bash\nexit 0\n' > "$STAGE/laplace-mcp"
chmod 0755 "$STAGE/laplace-mcp"
ln -s api.dll "$STAGE/current-api"
printf 'managed\n' > "$MCP_STAGE/Laplace.Endpoints.Mcp.dll"
printf '#!/usr/bin/env bash\nexit 0\n' > "$MCP_STAGE/Laplace.Endpoints.Mcp"
chmod 0755 "$MCP_STAGE/Laplace.Endpoints.Mcp"

sync_app() {
  laplace_sync_payload "$STAGE" "$APP_DIR" \
    --exclude 'laplace-api.env' --exclude 'logs/' --exclude 'mcp-runtime/'
}

assert_app_contract() {
  [[ "$(stat -c '%u:%g:%a' "$APP_DIR")" == "$APP_METADATA" ]] || {
    echo "app root metadata drifted to $(stat -c '%u:%g:%a' "$APP_DIR")" >&2
    return 1
  }
  [[ -f "$APP_DIR/laplace-api.env" ]]
  [[ -f "$APP_DIR/logs/app.csv" ]]
  [[ -x "$APP_DIR/laplace-mcp" ]]
  [[ -L "$APP_DIR/current-api" ]]
  [[ ! -e "$APP_DIR/stale.dll" ]]
  [[ "$(stat -c '%u:%g:%a' "$MCP_DIR")" == "$MCP_METADATA" ]]
}

laplace_sync_payload "$MCP_STAGE" "$MCP_DIR"
sync_app
assert_app_contract
[[ -x "$MCP_DIR/Laplace.Endpoints.Mcp" ]]
[[ "$(<"$APP_DIR/api.dll")" == "first" ]]

# A second publish must update/delete payload entries while preserving the host
# root and bootstrap-owned state exactly as the first publish did.
printf 'second\n' > "$STAGE/api.dll"
rm "$STAGE/wwwroot/index.html"
printf 'second\n' > "$STAGE/wwwroot/app.js"
sync_app
assert_app_contract
laplace_sync_payload "$MCP_STAGE" "$MCP_DIR"
assert_app_contract
[[ "$(<"$APP_DIR/api.dll")" == "second" ]]
[[ ! -e "$APP_DIR/wwwroot/index.html" ]]
[[ -f "$APP_DIR/wwwroot/app.js" ]]

echo "OK deploy payload sync preserves bootstrap-owned host metadata across repeat publishes"
