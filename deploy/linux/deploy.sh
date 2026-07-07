#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR=/opt/laplace/app
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> [1/3] build front-end (web/ -> dist)"
pushd "$REPO_ROOT/web" >/dev/null
npm ci --no-audit --no-fund
test -f openapi/openapi.json || { echo "::error::web/openapi/openapi.json missing — run pipeline.sh build first"; exit 1; }
echo "    generating src/api/types.gen.ts from openapi/openapi.json"
npm run gen:api
npm run build
popd >/dev/null

echo "==> [2/3] publish API -> staging ($STAGE)"
dotnet publish "$REPO_ROOT/app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj" \
  -c Release --no-self-contained -o "$STAGE"
rm -rf "$STAGE/wwwroot"
mkdir -p "$STAGE/wwwroot"
cp -r "$REPO_ROOT/web/dist/." "$STAGE/wwwroot/"

echo "==> [3/3] sync into $APP_DIR (preserve env + logs)"
rsync -a --delete \
  --exclude 'laplace-api.env' --exclude 'logs/' \
  "$STAGE/" "$APP_DIR/"
echo "✓ published API + SPA to $APP_DIR"
