#!/usr/bin/env bash
set -euo pipefail
: "${TMPDIR:?build scratch required}"
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/machine-config.XXXXXX")
chmod g-s "$work"
trap 'rm -rf "$work"' EXIT
sed -n '/^machine_config_permissions() {/,/^}/p' "$root/deploy/linux/site-host.sh" > "$work/functions"
. "$work/functions"
machine_config_permissions "$work/config"
[[ $(stat -c %a "$work/config") == 755 && ! -e "$work/config/machine.env" ]]
printf 'LAPLACE_SITE_PORT=8080\n' > "$work/config/machine.env"
printf 'fixture credential\n' > "$work/config/pgpass"
cp "$work/config/pgpass" "$work/config/backup.key"
chmod 700 "$work/config"
chmod 600 "$work/config/"*
before=$(sha256sum "$work/config/"*)
machine_config_permissions "$work/config"
machine_config_permissions "$work/config"
[[ $(stat -c %a "$work/config") == 755 ]]
[[ $(stat -c %a "$work/config/machine.env") == 644 ]]
[[ $(stat -c %a "$work/config/pgpass") == 600 ]]
[[ $(stat -c %a "$work/config/backup.key") == 600 ]]
[[ $(sha256sum "$work/config/"*) == "$before" ]]
echo 'PASS absent declaration, permission repair, repeated setup, unchanged credential modes and contents'
