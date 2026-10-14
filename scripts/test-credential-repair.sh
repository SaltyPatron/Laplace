#!/usr/bin/env bash
# Exercise publication/rollback with fixture files and a simulated service manager.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# shellcheck source=../deploy/linux/repair-credentials.sh
source "$ROOT/deploy/linux/repair-credentials.sh"
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT

# Root ownership is simulated; no system directory or real service is touched.
stat() { if [[ $1 == -c && $2 == '%u:%a' ]]; then echo 0:700; else command stat "$@"; fi; }
sleep() { :; }
systemctl() {
  printf '%s\n' "$*" >> "$case_root/commands"
  case "$1" in
    show)
      case "$4" in
        ExecStart) echo "$LAPLACE_IDENTITY_DIRECTORY/agent.hcl" ;;
        NRestarts) echo 0 ;;
        LoadState) echo loaded ;;
        Result) echo success ;;
        *) return 1 ;;
      esac ;;
    stop) return 0 ;;
    start)
      if [[ $2 == laplace-mono-identity.service ]]; then
        if [[ $scenario == fresh || $scenario == dependent_failure ]]; then
          jq -n --arg now "$(date -u '+%Y-%m-%dT%H:%M:%S.%NZ')" \
            '{source:"openbao-agent",application_provider_configuration:"verified",verified_utc:$now}' \
            > "$LAPLACE_IDENTITY_DIRECTORY/receipt.json"
        fi
      elif [[ $scenario == dependent_failure ]]; then return 1
      fi ;;
    is-active) [[ $scenario != activation_failure ]] ;;
    *) return 1 ;;
  esac
}

for scenario in fresh activation_failure stale_receipt dependent_failure symlink; do
  case_root=$fixture/$scenario
  export LAPLACE_IDENTITY_DIRECTORY=$case_root/identity
  mkdir -p "$LAPLACE_IDENTITY_DIRECTORY"
  : > "$case_root/commands"
  printf 'old helper\n' > "$LAPLACE_IDENTITY_DIRECTORY/identity-runtime.py"
  cp "$LAPLACE_IDENTITY_DIRECTORY/identity-runtime.py" "$case_root/old"
  printf 'new helper\n' > "$case_root/new"
  jq -n --arg host "$(hostname -s)" '{host:($host|ascii_downcase)}' > "$LAPLACE_IDENTITY_DIRECTORY/enrollment.json"
  for file in role-id secret-id agent.hcl; do printf 'retained fixture\n' > "$LAPLACE_IDENTITY_DIRECTORY/$file"; done
  command sha256sum "$LAPLACE_IDENTITY_DIRECTORY/enrollment.json" "$LAPLACE_IDENTITY_DIRECTORY/role-id" \
    "$LAPLACE_IDENTITY_DIRECTORY/secret-id" > "$case_root/before.sha256"
  jq -n '{source:"openbao-agent",application_provider_configuration:"verified",verified_utc:"2000-01-01T00:00:00Z"}' > "$LAPLACE_IDENTITY_DIRECTORY/receipt.json"
  if [[ $scenario == symlink ]]; then
    rm "$LAPLACE_IDENTITY_DIRECTORY/identity-runtime.py"
    ln -s "$case_root/old" "$LAPLACE_IDENTITY_DIRECTORY/identity-runtime.py"
  fi
  # Run as a standalone process so Bash's errexit remains active inside repair.
  export case_root scenario
  export -f repair_credentials stat sleep systemctl
  if bash -c 'repair_credentials "$1"' bash "$case_root/new" > "$case_root/output" 2>&1; then result=0; else result=$?; fi
  command sha256sum --check --status "$case_root/before.sha256"
  case "$scenario" in
    fresh)
      [[ $result == 0 ]]
      cmp "$case_root/new" "$LAPLACE_IDENTITY_DIRECTORY/identity-runtime.py"
      rg -q 'laplace-database-refresh.service: passed' "$case_root/output" ;;
    dependent_failure)
      [[ $result != 0 ]]
      cmp "$case_root/new" "$LAPLACE_IDENTITY_DIRECTORY/identity-runtime.py"
      rg -q 'identity repair retained' "$case_root/output" ;;
    activation_failure|stale_receipt)
      [[ $result != 0 ]]
      cmp "$case_root/old" "$LAPLACE_IDENTITY_DIRECTORY/identity-runtime.py"
      rg -q 'restoring the preceding helper' "$case_root/output" ;;
    symlink)
      [[ $result != 0 && ! -s $case_root/commands ]]
      [[ $(cat "$case_root/old") == 'old helper' ]] ;;
  esac
  echo "$scenario: passed"
done
