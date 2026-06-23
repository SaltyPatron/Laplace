#!/usr/bin/env bash
# Publish the API + SPA into /opt/laplace/app. Publish-only: the deploy-app
# workflow owns build/install (native engine + extension), DB migrate, the seed
# ladder, and the service lifecycle. The caller MUST have stopped laplace-api
# before invoking this (so the rsync doesn't fight a running app).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR=/opt/laplace/app
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> [1/3] build front-end (web/ -> dist)"
pushd "$REPO_ROOT/web" >/dev/null
npm ci --no-audit --no-fund
npm run build
popd >/dev/null

echo "==> [2/3] publish API -> staging ($STAGE)"
dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj" \
  -c Release --no-self-contained -o "$STAGE"
# Overlay the freshly built SPA into the published wwwroot (single-origin).
rm -rf "$STAGE/wwwroot"
mkdir -p "$STAGE/wwwroot"
cp -r "$REPO_ROOT/web/dist/." "$STAGE/wwwroot/"

echo "==> [3/3] sync into $APP_DIR (preserve env + logs)"
rsync -a --delete \
  --exclude 'laplace-api.env' --exclude 'logs/' \
  "$STAGE/" "$APP_DIR/"
echo "✓ published API + SPA to $APP_DIR"
