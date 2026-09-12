#!/usr/bin/env bash
# Read-only operator view of the shared host's storage and release usage.
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init
printf 'Mounted storage and inode capacity\n'
df -hT / /build /var/lib/agents /opt/laplace /opt/laplace/pgdata /var/lib/pgwal /pgtemp /vault
df -i /opt/laplace/app
printf '\nApplication and package disk usage (separate mounts excluded)\n'
du -x -h -d 3 /opt/laplace 2>/dev/null | sort -h | tail -n 120 || true
printf '\nInstalled service identities and scratch settings\n'
for unit in laplace-api.service laplace-mcp.service laplace-lichess.service \
  actions.runner.SaltyPatron-Laplace.hart-server.service \
  actions.runner.SaltyPatron-Laplace-Refactor.hart-server-refactor.service \
  actions.runner.SaltyPatron-Laplace-Conventional.hart-server-conventional.service; do
  printf '%s\n' "$unit"
  systemctl show "$unit" -p User -p Group -p UMask -p ActiveState
  systemctl show "$unit" -p Environment --value | tr ' ' '\n' | rg '^(TMPDIR|TMP|TEMP)=' || true
done
printf '\nApplication release sizes and selected executables\n'
for release in /opt/laplace/app/releases/runtime.*; do
  [[ -d "$release" ]] || continue
  du -sh "$release"
  for link in /opt/laplace/app/laplace-{lichess,mcp,uci}; do
    [[ -L "$link" ]] || continue
    selected=$(readlink -f "$link")
    [[ "$selected" != "$release/"* ]] || printf '  %s -> %s\n' "$link" "$selected"
  done
  for process in /proc/[0-9]*; do
    [[ -r "$process/maps" ]] || continue
    if rg -F -q " $release/" "$process/maps" 2>/dev/null; then
      printf '  mapped by process %s\n' "${process##*/}"
    fi
  done
done
printf '\nManaged application backups\n'
find /opt/laplace/app-backups -mindepth 1 -maxdepth 1 -type d -name 'managed.*' \
  -exec du -sh {} + 2>/dev/null || true
printf '\nDeleted files still held open\n'
lsof +L1 /opt/laplace 2>/dev/null | tail -n 200 || true
printf '\nManaged process memory (arguments omitted)\n'
ps -eo pid,ppid,user,group,lstart,rss,comm | rg 'laplace|dotnet' || true
