#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

command="${1:-}"
shift || true
case "$command" in
  status|up|reset|nuke) ;;
  *) echo "usage: db-migrations.sh status|up|reset|nuke [migration args...]" >&2; exit 2 ;;
esac

candidates=()
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

exec dotnet "$runtime" "$command" "$@"
