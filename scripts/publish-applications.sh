#!/usr/bin/env bash
# Application-only release against a byte-verified, unchanged installed engine.
# Full engine/database/eval releases remain the existing `all` pipeline.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

application_guard() { python3 "$ROOT/scripts/check-application-runtime.py" "$@"; }
application_host_check() {
  [[ ! -e /var/lib/laplace-managed/transaction.json ]] || {
    echo "::error::prior managed transaction unresolved; no application changes made" >&2; return 1;
  }
  # preflight reconciles root-owned host state and then proves host-status. Checking
  # status first blocked the operation that repairs install-owned drift.
  application_managed preflight
}
application_managed() { bash "$ROOT/deploy/linux/managed-publish.sh" "$@"; }
application_publish() { bash "$ROOT/scripts/pipeline.sh" publish; }
application_restart() {
  local action
  action="$(<"$ROOT/build/.publish-action")"
  if [[ "$action" == skipped ]] && systemctl is-active --quiet laplace-api; then
    application_managed verify
  else
    sudo -n systemctl restart laplace-api
    application_managed activate
  fi
}
application_restore() {
  sudo -n systemctl start laplace-api
  python3 "$ROOT/scripts/verify-application-release.py" --readiness-only
}
application_stamp() { bash "$ROOT/scripts/pipeline.sh" publish-stamp; }
application_verify() {
  local prefix="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
  application_managed verify
  python3 "$ROOT/scripts/verify-application-release.py"
  python3 "$ROOT/scripts/check-uci-runtime.py" "$prefix/app/laplace-uci"
  python3 "$ROOT/scripts/test-cutechess-runtime.py" "$prefix/bin/cutechess-cli" \
    "$prefix/app/laplace-uci" "$prefix/bin/stockfish"
}

application_recover() {
  local owner="$1" marker="$ROOT/build/.application-publish-owner"
  local pending="$ROOT/build/.application-restore-pending"
  [[ -f "$marker" ]] || return 0
  [[ "$(<"$marker")" == "$owner" ]] || {
    echo "::error::recovery receipt belongs to another run; no changes made" >&2; return 1;
  }
  if [[ -f "$ROOT/build/.managed-publish-backup" ]]; then
    printf '%s' "$owner" > "$pending"
    application_managed rollback || return 1
  fi
  if [[ -f "$pending" ]]; then
    [[ "$(<"$pending")" == "$owner" ]] || return 1
    application_restore || return 1
    rm "$pending"
  fi
  rm -f "$ROOT/build/.application-release-state.json"
  rm "$marker"
}

application_main() (
  set -euo pipefail
  local mode="${1:-}" attempted=0 proof rc=0 owner="${GITHUB_RUN_ID:-local-$$}"
  if [[ "$mode" == recover ]]; then
    application_recover "$owner"
    exit 0
  fi
  [[ "$mode" == check || "$mode" == deploy ]] || {
    echo "usage: publish-applications.sh check|deploy|recover" >&2; exit 2;
  }
  [[ ! -e "$ROOT/build/.managed-publish-backup" && ! -e "$ROOT/build/.application-publish-owner" \
     && ! -e "$ROOT/build/.application-restore-pending" ]] || {
    echo "::error::prior publish receipt unresolved; no application changes made" >&2; exit 1;
  }
  proof="$(mktemp -d)"
  trap 'rc=$?; trap - EXIT
    if [[ "$attempted" == 1 ]]; then
      application_recover "$owner" || rc=1
    fi
    rm -rf "$proof"
    exit "$rc"' EXIT
  trap 'exit 143' TERM HUP
  trap 'exit 130' INT
  application_guard --snapshot "$proof/runtime-before.json"
  application_host_check
  if [[ "$mode" == check ]]; then
    echo "PASS: application release preflight; no deployment performed"
    exit 0
  fi
  mkdir -p "$ROOT/build"
  rm -f "$ROOT/build/.application-release-state.json"
  (set -o noclobber; printf '%s' "$owner" > "$ROOT/build/.application-publish-owner")
  attempted=1
  application_publish
  application_restart
  application_verify
  application_guard --compare "$proof/runtime-before.json"
  application_managed commit
  attempted=0
  rm "$ROOT/build/.application-publish-owner"
  application_stamp
  cp "$proof/runtime-before.json" "$ROOT/build/.applications-verified.json"
  echo "PASS: application release committed; native engine/database unchanged"
)

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  application_main "$@"
fi
