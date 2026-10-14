#!/usr/bin/env bash
# Deploy the owner's identity helper into an already enrolled installation.
# Invoked by site-host.sh --repair-credentials; never creates an enrollment.
set -euo pipefail

repair_credentials() (
  set -euo pipefail
  local directory=${LAPLACE_IDENTITY_DIRECTORY:-/etc/laplace-mono/identity}
  local source=$1 service=laplace-mono-identity.service backup staged='' changed=false
  local started restarts observed fresh=false unit failed=false host
  for command in jq stat install mktemp cp mv systemctl date sha256sum; do
    command -v "$command" >/dev/null || { echo "Missing prerequisite: $command" >&2; exit 1; }
  done
  [[ $directory == /* && $directory != *'/../'* && ! -L $directory && -d $directory ]] || {
    echo 'Existing real identity directory required.' >&2; exit 1;
  }
  [[ $(stat -c '%u:%a' "$directory") == 0:700 ]] || {
    echo 'Identity directory must remain root-owned mode 0700.' >&2; exit 1;
  }
  for file in enrollment.json role-id secret-id agent.hcl identity-runtime.py; do
    [[ -f $directory/$file && ! -L $directory/$file ]] || {
      echo "Missing or symlinked installed identity file: $file" >&2; exit 1;
    }
  done
  [[ -f $source && ! -L $source ]] || { echo 'Owner identity helper missing.' >&2; exit 1; }
  host=$(hostname -s)
  jq -e --arg host "${host,,}" '.host == $host' "$directory/enrollment.json" >/dev/null || {
    echo 'Enrollment belongs to another host.' >&2; exit 1;
  }
  [[ $(systemctl show "$service" -p ExecStart --value) == *"$directory/agent.hcl"* ]] || {
    echo 'Installed identity unit does not select this directory.' >&2; exit 1;
  }
  backup=$(mktemp -d "$directory/repair-XXXXXXXX")
  cp -p "$directory/identity-runtime.py" "$backup/identity-runtime.py"
  # Fingerprints only: no enrollment or provider values enter the receipt.
  sha256sum "$directory/enrollment.json" "$directory/role-id" "$directory/secret-id" > "$backup/enrollment.sha256"
  finish() {
    local result=$?
    trap - EXIT
    if [[ -n $staged ]]; then rm -f -- "$staged"; fi
    if (( result != 0 )) && "$changed"; then
      echo 'Identity activation failed; restoring the preceding helper.' >&2
      systemctl stop "$service" || true
      install -m 0700 "$backup/identity-runtime.py" "$directory/identity-runtime.py"
      systemctl start "$service" || true
    fi
    echo "Repair evidence and preceding helper: $backup"
    exit "$result"
  }
  trap finish EXIT
  systemctl stop "$service"
  changed=true
  staged=$(mktemp "$directory/.identity-runtime-XXXXXXXX")
  install -m 0700 "$source" "$staged"
  mv -f -- "$staged" "$directory/identity-runtime.py"
  staged=''
  started=$(date -u +%s%N)
  systemctl start "$service"
  restarts=$(systemctl show "$service" -p NRestarts --value)
  # A fresh agent receipt proves authenticated rendering and provider checks.
  # Observe beyond the old 30-second failure plus 15-second restart interval.
  for ((attempt=0; attempt<12; attempt++)); do
    sleep 5
    systemctl is-active --quiet "$service" || exit 1
    [[ $(systemctl show "$service" -p NRestarts --value) == "$restarts" ]] || {
      echo 'Identity agent restarted during verification.' >&2; exit 1;
    }
    if [[ -f $directory/receipt.json ]] &&
       jq -e '.source == "openbao-agent" and .application_provider_configuration == "verified"' "$directory/receipt.json" >/dev/null; then
      observed=$(jq -r '.verified_utc' "$directory/receipt.json")
      observed=$(date -u -d "$observed" +%s%N)
      if (( observed >= started )); then fresh=true; fi
    fi
  done
  "$fresh" || { echo 'No fresh authenticated identity receipt.' >&2; exit 1; }
  sha256sum --check --status "$backup/enrollment.sha256" || {
    echo 'Enrollment changed during repair; verification failed.' >&2; exit 1;
  }
  sha256sum "$directory/identity-runtime.py" > "$backup/installed-helper.sha256"
  date -u '+identity verified %Y-%m-%dT%H:%M:%SZ' > "$backup/result"
  changed=false
  echo 'Identity helper installed; authenticated rendering verified; no restart during 60-second observation.'
  # Run existing enrolled jobs. Do not recreate identities or export credentials.
  for unit in laplace-database-refresh.service laplace-mono-chess-client-certificate.service; do
    if [[ $(systemctl show "$unit" -p LoadState --value) == loaded ]]; then
      if systemctl start "$unit" && [[ $(systemctl show "$unit" -p Result --value) == success ]]; then
        echo "$unit: passed" | tee -a "$backup/result"
      else
        echo "$unit: failed; identity repair retained" | tee -a "$backup/result" >&2
        failed=true
      fi
    fi
  done
  ! "$failed"
)

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
  [[ $(id -u) == 0 ]] || { echo 'Run through sudo bash deploy/linux/site-host.sh --repair-credentials' >&2; exit 1; }
  repair_credentials "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/identity-runtime.py"
fi
