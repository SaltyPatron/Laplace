#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/database.sh"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

command="${1:-}"
shift || true
case "$command" in
  status|up|reset|nuke|app-up|app-status|app-check|app-reset|app-nuke|substrate-up|substrate-status|supports) ;;
  *) echo "usage: db-migrations.sh status|up|reset|nuke|app-up|app-status|app-check|app-reset|app-nuke|substrate-up|substrate-status [migration args...]" >&2; exit 2 ;;
esac

candidates=()
candidates+=("${LAPLACE_APP_DIR:-/opt/laplace/app}/migrations/Laplace.Migrations.dll")
if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
  candidates+=("$LAPLACE_BUILD_ROOT/app/bin/Laplace.Migrations/Release/net10.0/Laplace.Migrations.dll")
fi
candidates+=("$ROOT/app/Laplace.Migrations/bin/Release/net10.0/Laplace.Migrations.dll")

runtime=""
for candidate in "${candidates[@]}"; do
  if [[ -f "$candidate" ]]; then
    runtime="$candidate"
    break
  fi
done

[[ -n "$runtime" ]] || {
  echo "::error::prebuilt Laplace.Migrations runtime not found" >&2
  echo "::error::build/deploy owns compilation; database operations never compile" >&2
  exit 1
}

supports() {
  local help requested
  [[ $# -gt 0 ]] || return 2
  help=$(dotnet "$runtime" --help) || return
  for requested in "$@"; do
    if [[ ! "$requested" =~ ^[a-z-]+$ ]] || ! grep -Eq "^[[:space:]]*$requested([[:space:]]|$)" <<< "$help"; then
      echo "Installed migration runtime does not support $requested; publish the matching migration runtime first." >&2
      return 2
    fi
  done
}
case "$command" in
  supports) supports "$@"; exit ;;
  app-*|substrate-*) supports "$command" || exit ;;
esac

case "$command" in
  app-*) exec python3 "$ROOT/deploy/linux/platform-owner-policy.py" -- dotnet "$runtime" "$command" "$@" ;;
  *) exec dotnet "$runtime" "$command" "$@" ;;
esac
