#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=scripts/test-suites/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

run_managed_dev() {
  local selected="${LAPLACE_MANAGED_TEST_PROJECTS:-all}" project
  set_dev_perfcache
  sync_managed_native

  if [[ "$selected" == all ]]; then
    dotnet test app/Laplace.slnx -c Release --no-build --nologo --verbosity minimal \
      --filter 'Tier!=db&Tier!=live&Tier!=perf'
    return
  fi

  [[ -n "$selected" ]] || {
    echo "::notice::managed dependency graph selected no managed test projects"
    return 0
  }

  IFS=',' read -r -a projects <<< "$selected"
  for project in "${projects[@]}"; do
    [[ "$project" == app/*.csproj || "$project" == app/*/*.csproj ]] || {
      echo "::error::managed test project is outside app/: $project" >&2
      return 2
    }
    [[ -f "$ROOT/$project" ]] || {
      echo "::error::managed test project does not exist: $project" >&2
      return 2
    }
    echo "--- managed test target: $project"
    dotnet test "$ROOT/$project" -c Release --no-build --nologo --verbosity minimal \
      --filter 'Tier!=db&Tier!=live&Tier!=perf'
  done
}

run_managed_dev
