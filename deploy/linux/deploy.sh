#!/usr/bin/env bash
# Routine Laplace API deploy on hart-server. Run as laplace-runner (this is what
# the GitHub Actions deploy-app workflow invokes on the self-hosted runner).
# Unprivileged except for the narrow `sudo systemctl/nginx` grants installed by
# bootstrap-host.sh. Assumes `just build && just install` already ran (native
# engine + extension installed under /opt/laplace).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR=/opt/laplace/app
STAGE="$(mktemp -d)"
PGDB="${PGDATABASE:-laplace}"
export LD_LIBRARY_PATH="/opt/laplace/lib:${LD_LIBRARY_PATH:-}"
trap 'rm -rf "$STAGE"' EXIT

echo "==> [1/5] build front-end (web/ -> dist)"
pushd "$REPO_ROOT/web" >/dev/null
npm ci --no-audit --no-fund
npm run build
popd >/dev/null

echo "==> [2/5] publish API -> staging ($STAGE)"
dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj" \
  -c Release --no-self-contained -o "$STAGE"
# Overlay the freshly built SPA into the published wwwroot (single-origin).
rm -rf "$STAGE/wwwroot"
mkdir -p "$STAGE/wwwroot"
cp -r "$REPO_ROOT/web/dist/." "$STAGE/wwwroot/"

echo "==> [3/5] ensure database '$PGDB' + extension + migrations"
PGDATABASE="$PGDB" dotnet "$REPO_ROOT/app/Laplace.Migrations/bin/Release/net10.0/Laplace.Migrations.dll" up

echo "==> [4/5] swap into $APP_DIR (preserving env + logs) and restart"
sudo systemctl stop laplace-api 2>/dev/null || true
# rsync staging over the live dir; never clobber the env file or logs.
rsync -a --delete \
  --exclude 'laplace-api.env' --exclude 'logs/' \
  "$STAGE/" "$APP_DIR/"
sudo systemctl start laplace-api

echo "==> [5/5] health check"
sleep 2
for i in $(seq 1 15); do
  if curl -fsS "http://127.0.0.1:5187/health" >/dev/null 2>&1; then
    echo "✓ API healthy on 127.0.0.1:5187 (LAN: http://hart-server:8080)"; exit 0
  fi
  sleep 1
done
echo "✗ API did not become healthy; recent logs:"; journalctl -u laplace-api -n 40 --no-pager || true
exit 1
