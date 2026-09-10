#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/deploy/linux/payload-sync.sh"
source "$ROOT/deploy/linux/app-dir-contract.sh"

TEST_ROOT="$(mktemp -d)"
trap 'chmod -R u+w "$TEST_ROOT" 2>/dev/null || true; rm -rf "$TEST_ROOT"' EXIT

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
printf 'shared-runtime\n' > "$LICHESS_STAGE/shared-runtime.dll"
printf '#!/usr/bin/env bash\nexit 0\n' > "$UCI_STAGE/laplace-uci"
chmod 0755 "$UCI_STAGE/laplace-uci"
for suffix in dll deps.json runtimeconfig.json; do
  printf 'first-uci\n' > "$UCI_STAGE/laplace-uci.$suffix"
done
printf 'dependency\n' > "$UCI_STAGE/chess-dependency.dll"
old_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"

# Select the first immutable release exactly as production does. The second staging
# operation must retain the old release for existing processes while hardlinking every
# byte-identical file into the new release rather than requiring a second full copy.
rm -f "$APP_DIR/laplace-mcp"
ln -s "releases/$(basename "$old_release")/mcp/Laplace.Endpoints.Mcp" "$APP_DIR/laplace-mcp"
ln -s "releases/$(basename "$old_release")/lichess/Laplace.Endpoints.Lichess" "$APP_DIR/laplace-lichess"
ln -s "releases/$(basename "$old_release")/uci/laplace-uci" "$APP_DIR/laplace-uci"
old_shared_inode="$(stat -c '%d:%i' "$old_release/lichess/shared-runtime.dll")"
old_mcp_host_inode="$(stat -c '%d:%i' "$old_release/mcp/Laplace.Endpoints.Mcp")"
old_mcp_native_inode="$(stat -c '%d:%i' "$old_release/mcp/Laplace.Endpoints.Mcp.native")"

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
[[ "$(stat -c '%d:%i' "$new_release/lichess/shared-runtime.dll")" == "$old_shared_inode" ]]
[[ "$(stat -c '%d:%i' "$new_release/mcp/Laplace.Endpoints.Mcp")" == "$old_mcp_host_inode" ]]
[[ "$(stat -c '%d:%i' "$new_release/mcp/Laplace.Endpoints.Mcp.native")" == "$old_mcp_native_inode" ]]
[[ "$(stat -c '%d:%i' "$new_release/mcp/Laplace.Endpoints.Mcp.dll")" != \
   "$(stat -c '%d:%i' "$old_release/mcp/Laplace.Endpoints.Mcp.dll")" ]]
[[ -x "$APP_DIR/laplace-uci" ]]
echo "OK repeat managed publishes preserve existing clients and hardlink unchanged immutable payloads"

if failed_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$TEST_ROOT/missing-stage" "$LICHESS_STAGE" "$UCI_STAGE")"; then
  echo "missing apphost was accepted: $failed_release" >&2
  exit 1
fi

# A failed copy happens only after runtime.XXXXXX has been allocated. It must
# remove that exact partial release before returning failure; otherwise each
# ENOSPC retry consumes more of the application LV and guarantees the next retry
# also fails. Count release directories before and after the injected failure.
release_count_before="$(find "$APP_DIR/releases" -mindepth 1 -maxdepth 1 -type d -name 'runtime.*' | wc -l)"
if (
  laplace_sync_payload() { return 23; }
  failed_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"
); then
  echo "failed runtime copy was accepted" >&2
  exit 1
fi
release_count_after="$(find "$APP_DIR/releases" -mindepth 1 -maxdepth 1 -type d -name 'runtime.*' | wc -l)"
[[ "$release_count_after" == "$release_count_before" ]] || {
  echo "failed runtime staging leaked a release directory: before=$release_count_before after=$release_count_after" >&2
  exit 1
}
echo "OK failed managed runtime staging reclaims its partial release"

for suffix in dll deps.json runtimeconfig.json; do
  mv "$UCI_STAGE/laplace-uci.$suffix" "$TEST_ROOT/missing-uci-file"
  if failed_release="$(laplace_stage_managed_runtimes "$APP_DIR" "$MCP_STAGE" "$LICHESS_STAGE" "$UCI_STAGE")"; then
    echo "incomplete UCI runtime accepted without $suffix: $failed_release" >&2
    exit 1
  fi
  mv "$TEST_ROOT/missing-uci-file" "$UCI_STAGE/laplace-uci.$suffix"
done
echo "OK apphost-only and incomplete UCI packages are rejected"

# Garbage collection must continue past an inaccessible old layout. The first
# fixture is intentionally incomplete but has a non-writable child directory,
# matching the production failure where an older runtime contained service/root
# owned files. It must be left completely intact while the later runner-owned
# incomplete release is reclaimed.
GC_APP="$TEST_ROOT/gc-app"
inaccessible="$GC_APP/releases/runtime.inaccessible"
mkdir -p "$inaccessible/mcp"
printf 'protected\n' > "$inaccessible/mcp/Laplace.Agents.dll"
chmod 0555 "$inaccessible/mcp"
mkdir -p "$GC_APP/releases/runtime.incomplete/mcp"
printf '#!/usr/bin/env bash\nexit 0\n' > "$GC_APP/releases/runtime.incomplete/mcp/Laplace.Endpoints.Mcp"
chmod 0755 "$GC_APP/releases/runtime.incomplete/mcp/Laplace.Endpoints.Mcp"
legacy="$GC_APP/releases/runtime.legacy"
mkdir -p "$legacy/mcp" "$legacy/lichess" "$legacy/uci"
for exe in "$legacy/mcp/Laplace.Endpoints.Mcp" "$legacy/lichess/Laplace.Endpoints.Lichess" "$legacy/uci/laplace-uci"; do
  printf '#!/usr/bin/env bash\nexit 0\n' > "$exe"
  chmod 0755 "$exe"
done
for suffix in dll deps.json runtimeconfig.json; do
  printf 'legacy\n' > "$legacy/uci/laplace-uci.$suffix"
done
laplace_prune_unreferenced_releases "$GC_APP"
[[ -f "$inaccessible/mcp/Laplace.Agents.dll" ]]
[[ ! -e "$GC_APP/releases/runtime.incomplete" ]]
[[ -d "$legacy" ]]
chmod 0755 "$inaccessible/mcp"
echo "OK release GC skips inaccessible old layouts, reclaims later failed debris, and preserves complete legacy runtimes"

# Completed transaction backups are not an archive. Preserve exactly the active
# rollback receipt when one is supplied, then reclaim it after the transaction ends.
BACKUP_ROOT="$TEST_ROOT/app-backups"
mkdir "$BACKUP_ROOT"
mkdir "$BACKUP_ROOT/managed.old-a" "$BACKUP_ROOT/managed.old-b" "$BACKUP_ROOT/managed.active" "$BACKUP_ROOT/not-managed"
printf 'old\n' > "$BACKUP_ROOT/managed.old-a/payload"
printf 'old\n' > "$BACKUP_ROOT/managed.old-b/payload"
printf 'active\n' > "$BACKUP_ROOT/managed.active/payload"
laplace_prune_managed_backups "$BACKUP_ROOT" "$BACKUP_ROOT/managed.active"
[[ ! -e "$BACKUP_ROOT/managed.old-a" && ! -e "$BACKUP_ROOT/managed.old-b" ]]
[[ -f "$BACKUP_ROOT/managed.active/payload" ]]
[[ -d "$BACKUP_ROOT/not-managed" ]]
laplace_prune_managed_backups "$BACKUP_ROOT"
[[ ! -e "$BACKUP_ROOT/managed.active" && -d "$BACKUP_ROOT/not-managed" ]]
if laplace_prune_managed_backups "$BACKUP_ROOT" "$TEST_ROOT/outside-managed"; then
  echo "backup pruner accepted a protection path outside its root" >&2
  exit 1
fi
echo "OK completed managed publish backups are bounded by transaction ownership"

# This script already owns the deploy payload policy gate in CI. Keep the MCP
# protocol regression under the same gate so a missing/stale probe cannot be
# skipped merely because no workflow file changed.
python3 "$ROOT/scripts/test-mcp-deploy-contract.py"
