#!/usr/bin/env bash
set -euo pipefail
: "${TMPDIR:?set TMPDIR to build scratch}"
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/app-migration-check.XXXXXX")
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
dotnet() {
  [[ "$1" == "$SITE_WORK/stage/app/migrations/Laplace.Migrations.dll" && "$2" == app-up ]]
  printf 'migrate\n' >> "$SITE_WORK/order"
  return "${MIGRATION_EXIT:-0}"
}
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
sed -n '/^phase_receive() {/,/^}/p' "$root/deploy/linux/site.sh" >> "$work/functions.sh"
mkdir -p "$work/bundle/app/wwwroot" "$work/bundle/share/laplace" "$work/installed/share/laplace"
cp "$work/stage/app/.laplace-source-revision" "$work/bundle/app/"
cp "$work/stage/app/wwwroot/.laplace-web-source-revision" "$work/bundle/app/wwwroot/"
touch "$work/bundle/app/Laplace.Endpoints.OpenAICompat.dll"
printf new > "$work/bundle/share/laplace/cache"
printf old > "$work/installed/share/laplace/cache"
LAPLACE_INSTALL_PREFIX="$work/installed" bash -c 'set -euo pipefail; fail() { echo "$*" >&2; exit 1; }; say() { :; }; source "$SITE_WORK/functions.sh"; phase_receive' > "$work/receive-result"
[[ "$(cat "$work/installed/share/laplace/cache")" == old ]]
[[ "$(cat "$work/stage/share/laplace/cache")" == new ]]
echo 'PASS receive stages caches without replacing the serving copy'
