#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_uci_dev() {
  local runtime output
  set_dev_perfcache
  sync_managed_native
  runtime=$(mktemp -d)
  trap 'rm -rf "$runtime"' RETURN
  dotnet publish app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj -c Release --no-build --no-self-contained -o "$runtime" --nologo
  output=$(printf 'uci\nisready\nquit\n' | timeout 30 "$runtime/laplace-uci")
  grep -q '^uciok$' <<<"$output"
  grep -q '^readyok$' <<<"$output"
  trap - RETURN
  rm -rf "$runtime"
}

run_uci_dev
