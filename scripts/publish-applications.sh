#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

managed() { bash "$ROOT/deploy/linux/managed-publish.sh" "$@"; }

recover() {
  local rc=0
  managed rollback || rc=$?
  sudo -n systemctl start laplace-api || rc=1
  return "$rc"
}

main() {
  local mode="${1:-}"
  case "$mode" in
    check)
      managed preflight
      echo "application publish preflight OK"
      ;;
    recover)
      recover
      ;;
    deploy)
      managed preflight
      trap 'rc=$?; trap - EXIT; recover || rc=1; exit "$rc"' EXIT INT TERM HUP
      bash "$ROOT/scripts/pipeline.sh" publish
      sudo -n systemctl restart laplace-api
      managed activate
      for _ in $(seq 1 60); do
        if curl -fsS http://127.0.0.1:5187/health/ready | grep -q '"ready":true'; then
          managed commit
          trap - EXIT INT TERM HUP
          echo "application publish committed"
          return 0
        fi
        sleep 1
      done
      echo "::error::laplace-api did not become ready after publish" >&2
      return 1
      ;;
    *)
      echo "usage: publish-applications.sh check|deploy|recover" >&2
      return 2
      ;;
  esac
}

main "$@"
