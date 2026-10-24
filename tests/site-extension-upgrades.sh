#!/usr/bin/env bash
set -euo pipefail
: "${TMPDIR:?build scratch required}"
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/site-extension-upgrades.XXXXXX")
trap 'rm -rf "$work"' EXIT
sed -n '/^phase_extensions() {/,/^}/p' "$root/deploy/linux/site.sh" > "$work/functions"
source "$work/functions"
EXT_SHARE=$work/share
LAPLACE_DBNAME=declared_fixture
mkdir -p "$EXT_SHARE/extension"
printf 'geom bridge\n' > "$EXT_SHARE/extension/laplace_geom_upgrade.sql"
printf 'substrate bridge\n' > "$EXT_SHARE/extension/laplace_substrate_upgrade.sql"
need_pg(){ :; }
say(){ :; }
fail(){ echo "$*" >&2; exit 1; }
sql(){
  case "$*" in
    *"default_version"*"'laplace'"*) echo 1.26;;
    *"extversion"*"'laplace'"*) echo 1.24;;
    *"default_version"*) echo new_hash;;
    *"extversion"*) echo old_hash;;
    *"ALTER EXTENSION"*) printf '%s\n' "$*" >> "$work/applied";;
    *) fail "unexpected SQL operation: $*";;
  esac
}
phase_extensions
grep -q "ALTER EXTENSION laplace UPDATE TO '1.26'" "$work/applied"
[[ ! -e "$EXT_SHARE/extension/laplace--1.24--1.26.sql" ]]
cmp "$EXT_SHARE/extension/laplace_geom_upgrade.sql" "$EXT_SHARE/extension/laplace_geom--old_hash--new_hash.sql"
cmp "$EXT_SHARE/extension/laplace_substrate_upgrade.sql" "$EXT_SHARE/extension/laplace_substrate--old_hash--new_hash.sql"
[[ $(wc -l < "$work/applied") == 3 ]]
echo 'PASS Native uses its version chain; monorepo extensions retain their hash bridges'
