#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=scripts/test-suites/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_live() {
  set_installed_perfcache
  sync_managed_native
  dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal --filter 'Tier=live'
}

run_managed_live
