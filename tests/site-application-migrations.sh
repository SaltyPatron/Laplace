#!/usr/bin/env bash
set -euo pipefail
: "${TMPDIR:?set TMPDIR to build scratch}"
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/app-migration-check.XXXXXX")
export ROOT="$root"
unset LAPLACE_PLATFORM_OWNER_POLICY_FILE
export LAPLACE_WORK_ROOT="$work/no-owner-policy"
trap 'rm -rf "$work"' EXIT
sed -n '/^revision() {/,/^}/p; /^phase_migrate_app() {/,/^}/p' "$root/deploy/linux/site.sh" > "$work/functions.sh"
mkdir -p "$work/stage/app/wwwroot" "$work/stage/app/migrations/db/migrations"
revision=1111111111111111111111111111111111111111
printf '%s\n' "$revision" > "$work/stage/app/.laplace-source-revision"
printf '%s\n' "$revision" > "$work/stage/app/wwwroot/.laplace-web-source-revision"
cat > "$work/run.sh" <<'SCRIPT'
set -euo pipefail
fail() { echo "$*" >&2; exit 1; }
source "$SITE_WORK/functions.sh"
mkdir -p "$SITE_WORK/bin"
cat > "$SITE_WORK/bin/dotnet" <<'DOTNET'
#!/usr/bin/env bash
  [[ "$1" == "$SITE_WORK/stage/app/migrations/Laplace.Migrations.dll" && "$2" == app-up ]]
  printf 'migrate\n' >> "$SITE_WORK/order"
  exit "${MIGRATION_EXIT:-0}"
DOTNET
chmod +x "$SITE_WORK/bin/dotnet"
export PATH="$SITE_WORK/bin:$PATH"
phase_migrate_app
printf 'publish\n' >> "$SITE_WORK/order"
SCRIPT
export SITE_WORK="$work" GITHUB_SHA="$revision"
if env -u LAPLACE_APP_DB bash "$work/run.sh" > "$work/result" 2>&1; then echo 'FAIL accepted missing application database'; exit 1; fi
[[ ! -e "$work/order" ]]
echo 'PASS missing application database blocks migration and publication'
if LAPLACE_APP_DB=fixture MIGRATION_EXIT=1 bash "$work/run.sh" > "$work/result" 2>&1; then echo 'FAIL accepted migration failure'; exit 1; fi
[[ "$(cat "$work/order")" == migrate ]]
echo 'PASS failed migration blocks publication'
rm "$work/order"
LAPLACE_APP_DB=fixture bash "$work/run.sh"
[[ "$(cat "$work/order")" == $'migrate\npublish' ]]
echo 'PASS successful staged migration precedes publication'
# Receiving a release must not replace serving caches before migrations succeed.
sed -n '/^phase_receive() {/,/^}/p; /^verify_cli_package() {/,/^}/p' "$root/deploy/linux/site.sh" >> "$work/functions.sh"
mkdir -p "$work/bundle/app/wwwroot" "$work/bundle/share/laplace" "$work/installed/share/laplace"
cp "$work/stage/app/.laplace-source-revision" "$work/bundle/app/"
cp "$work/stage/app/wwwroot/.laplace-web-source-revision" "$work/bundle/app/wwwroot/"
touch "$work/bundle/app/Laplace.Endpoints.OpenAICompat.dll"
mkdir -p "$work/bundle/app/cli/share/laplace"
for cache in laplace_t0_perfcache laplace_highway_perfcache laplace_chess_position_perfcache; do
  printf fixture > "$work/bundle/app/cli/share/laplace/$cache.bin"
done
printf '#!/bin/sh\nexit 0\n' > "$work/bundle/app/cli/Laplace.Cli"
chmod 755 "$work/bundle/app/cli/Laplace.Cli"
printf new > "$work/bundle/share/laplace/cache"
printf old > "$work/installed/share/laplace/cache"
tar -cf "$work/bundle/laplace-site.tar" -C "$work/bundle" app share
LAPLACE_INSTALL_PREFIX="$work/installed" bash -c 'set -euo pipefail; fail() { echo "$*" >&2; exit 1; }; say() { :; }; source "$SITE_WORK/functions.sh"; phase_receive' > "$work/receive-result"
[[ "$(cat "$work/installed/share/laplace/cache")" == old ]]
[[ "$(cat "$work/stage/share/laplace/cache")" == new ]]
[[ -x "$work/stage/app/cli/Laplace.Cli" ]]
echo 'PASS receive stages caches without replacing the serving copy'
rm "$work/stage/app/cli/share/laplace/laplace_highway_perfcache.bin"
if bash -c 'set -euo pipefail; source "$SITE_WORK/functions.sh"; verify_cli_package "$SITE_WORK/stage/app/cli"' > "$work/missing-cache-result" 2>&1; then
  echo 'FAIL accepted CLI package without its highway cache'; exit 1
fi
grep -q 'missing laplace_highway_perfcache.bin' "$work/missing-cache-result"
echo 'PASS missing CLI cache is rejected even when its executable starts'

# Selecting a restricted substrate identity does not change the application login.
sed -n '/^export PGHOST=/,/^export LAPLACE_EXTERNAL=/p' "$root/deploy/linux/site.sh" > "$work/connections.sh"
(
  unset LAPLACE_APP_ROLE LAPLACE_APP_DB LAPLACE_APP_PGPORT
  export LAPLACE_ROLE=application_owner LAPLACE_DATABASE_ROLE=substrate_runtime
  export LAPLACE_PGHOST=substrate.invalid LAPLACE_PGPORT=5432 LAPLACE_DBNAME=substrate
  export LAPLACE_APP_DBNAME=application LAPLACE_APP_PGHOST=127.0.0.1 LAPLACE_DB_SSLMODE=Require
  source "$work/connections.sh"
  [[ "$LAPLACE_DB" == 'Host=substrate.invalid;Port=5432;Username=substrate_runtime;Database=substrate;SSL Mode=Require' ]]
  [[ "$LAPLACE_APP_DB" == 'Host=127.0.0.1;Port=5432;Username=application_owner;Database=application' ]]
)
echo 'PASS substrate selection preserves the distinct application identity'

sed -n '/^api_env() {/,/^}/p' "$root/deploy/linux/site.sh" >> "$work/functions.sh"
mkdir -p "$work/runtime" "$work/prefix/share/laplace"
touch "$work/runtime/laplace-api.env" "$work/prefix/share/laplace/laplace_t0_perfcache_17.bin"
export LAPLACE_APP_DIR="$work/runtime" LAPLACE_INSTALL_PREFIX="$work/prefix" LAPLACE_API_URL=http://127.0.0.1:5187 LAPLACE_EXTERNAL="$work/external"
export LAPLACE_DB='Host=substrate;Username=substrate_runtime;Database=substrate'
export LAPLACE_APP_DB='Host=application;Username=schema_owner;Database=app'
export LAPLACE_APP_RUNTIME_DB='Host=application;Username=runtime;Database=app;Maximum Pool Size=2'
export LAPLACE_DATA="$work/corpus" LAPLACE_DATA_ROOT="$work/state"
bash -c 'set -euo pipefail; fail(){ exit 1; }; source "$SITE_WORK/functions.sh"; api_env; [[ "$LAPLACE_APP_DB" == *Username=schema_owner* ]]'
grep -Fxq "LAPLACE_APP_DB=$LAPLACE_APP_RUNTIME_DB" "$work/runtime/laplace-api.env"
grep -Fxq "LAPLACE_DATA=$LAPLACE_DATA" "$work/runtime/laplace-api.env"
grep -Fxq "LAPLACE_INSTALL_PREFIX=$LAPLACE_INSTALL_PREFIX" "$work/runtime/laplace-api.env"
echo 'PASS runtime configuration uses the restricted login without changing the migration identity'
if env -u LAPLACE_APP_DB bash -c 'set -euo pipefail; fail(){ exit 1; }; source "$SITE_WORK/functions.sh"; api_env'; then
  echo 'FAIL accepted runtime identity without a declared migration target'; exit 1
fi
echo 'PASS incomplete application identity declaration is rejected'

# Runtime substrate and MCP use their own identities; migration retains DDL access.
export LAPLACE_DB='Host=/tmp;Username=migration_owner;Database=substrate'
export LAPLACE_DATABASE_RUNTIME_DB='Host=127.0.0.1;Username=api_substrate;Database=substrate;SSL Mode=Require;Maximum Pool Size=2'
export LAPLACE_MCP_RUNTIME_DB='Host=/tmp;Username=mcp_substrate;Database=substrate;Maximum Pool Size=2'
export LAPLACE_MCP_LOG_DIR="$work/mcp-state/logs" LAPLACE_MCP_HTTP_PORT=5190 LAPLACE_MCP_ORIGIN=https://mcp.example
export LAPLACE_MCP_STATE_DIR="$work/mcp-state"
export PGPORT=5432
sed -n '/^mcp_env() {/,/^}/p' "$root/deploy/linux/site.sh" >> "$work/functions.sh"
bash -c 'set -euo pipefail; fail(){ exit 1; }; source "$SITE_WORK/functions.sh"; api_env; mcp_env; [[ "$LAPLACE_DB" == *Username=migration_owner* ]]'
grep -Fxq "LAPLACE_DB=$LAPLACE_DATABASE_RUNTIME_DB" "$work/runtime/laplace-api.env"
grep -Fxq "LAPLACE_DB=$LAPLACE_MCP_RUNTIME_DB" "$work/runtime/laplace-mcp.env"
grep -Fxq "LAPLACE_OPS_LOG_DIR=$LAPLACE_MCP_LOG_DIR" "$work/runtime/laplace-mcp.env"
grep -Fxq "LAPLACE_AGENTS_CONFIG=$LAPLACE_MCP_STATE_DIR/agents.json" "$work/runtime/laplace-mcp.env"
grep -Fxq "LAPLACE_OUT=$LAPLACE_MCP_STATE_DIR/out" "$work/runtime/laplace-mcp.env"
echo 'PASS API/MCP substrate identities and MCP writable state remain separate from deployment'
