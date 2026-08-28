#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/deploy/linux/payload-sync.sh"
source "$ROOT/deploy/linux/app-dir-contract.sh"

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

# Legacy deploys copied mktemp's 0700 mode onto mcp-runtime. A current deploy
# converges mode-only drift without taking ownership repair away from bootstrap.
chmod 0700 "$MCP_DIR"
laplace_reconcile_app_dir_contract "$APP_DIR" "$(id -un)" "$(id -gn)"
assert_app_contract

echo "OK deploy payload sync preserves bootstrap-owned host metadata across repeat publishes"

LICHESS_STAGE="$TEST_ROOT/lichess-stage"
UCI_STAGE="$TEST_ROOT/uci-stage"
mkdir "$LICHESS_STAGE" "$UCI_STAGE"
printf '#!/usr/bin/env bash\nexit 0\n' > "$LICHESS_STAGE/Laplace.Endpoints.Lichess"
chmod 0755 "$LICHESS_STAGE/Laplace.Endpoints.Lichess"
printf '#!/usr/bin/env bash\nexit 0\n' > "$UCI_STAGE/laplace-uci"
chmod 0755 "$UCI_STAGE/laplace-uci"
for suffix in dll deps.json runtimeconfig.json; do
  printf 'first-uci\n' > "$UCI_STAGE/laplace-uci.$suffix"
done
printf 'dependency\n' > "$UCI_STAGE/chess-dependency.dll"
old_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"
printf 'second-version\n' > "$MCP_STAGE/Laplace.Endpoints.Mcp.dll"
printf 'second-uci\n' > "$UCI_STAGE/laplace-uci.dll"
new_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"
[[ "$old_release" != "$new_release" ]]
[[ "$(<"$old_release/mcp/Laplace.Endpoints.Mcp.dll")" == managed ]]
[[ "$(<"$new_release/mcp/Laplace.Endpoints.Mcp.dll")" == second-version ]]
[[ "$(<"$MCP_DIR/Laplace.Endpoints.Mcp.dll")" == managed ]]
[[ -x "$new_release/lichess/Laplace.Endpoints.Lichess" ]]
[[ "$(<"$old_release/uci/laplace-uci.dll")" == first-uci ]]
[[ "$(<"$new_release/uci/laplace-uci.dll")" == second-uci ]]
[[ "$(<"$new_release/uci/chess-dependency.dll")" == dependency ]]
[[ -s "$new_release/uci/laplace-uci.deps.json" && -s "$new_release/uci/laplace-uci.runtimeconfig.json" ]]
ln -s "releases/$(basename "$new_release")/uci/laplace-uci" "$APP_DIR/laplace-uci"
[[ -x "$APP_DIR/laplace-uci" ]]
echo "OK repeat managed publishes preserve existing clients' runtime directories"
if failed_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$TEST_ROOT/missing-stage" "$LICHESS_STAGE" "$UCI_STAGE")"; then
  echo "missing apphost was accepted: $failed_release" >&2
  exit 1
fi
# Deliberately break the copy implementation: even inside $(...), failure must
# propagate instead of the final path print falsely reporting a good release.
if (
  laplace_sync_payload() { return 23; }
  failed_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"
); then
  echo "failed runtime copy was accepted" >&2
  exit 1
fi
echo "OK failed managed runtime staging never reports a publishable release"
for suffix in dll deps.json runtimeconfig.json; do
  mv "$UCI_STAGE/laplace-uci.$suffix" "$TEST_ROOT/missing-uci-file"
  if failed_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"; then
    echo "incomplete UCI runtime accepted without $suffix: $failed_release" >&2
    exit 1
  fi
  mv "$TEST_ROOT/missing-uci-file" "$UCI_STAGE/laplace-uci.$suffix"
done
echo "OK apphost-only and incomplete UCI packages are rejected"
