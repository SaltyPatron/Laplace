#!/usr/bin/env bash

laplace_require_app_dir_contract() {
  local app_dir="$1"
  local expected_owner="${2:-laplace-runner}"
  local expected_group="${3:-laplace-runner}"
  local path owner group mode

  if [[ ! -d "$app_dir" ]]; then
    echo "::error::$app_dir missing — run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
    return 1
  fi

  for path in "$app_dir" "$app_dir/logs" "$app_dir/mcp-runtime"; do
    if [[ ! -d "$path" ]]; then
      echo "::error::$path missing — run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
      return 1
    fi
    owner="$(stat -c '%U' "$path")"
    group="$(stat -c '%G' "$path")"
    mode="$(stat -c '%a' "$path")"
    if [[ "$owner" != "$expected_owner" || "$group" != "$expected_group" || "$mode" != "2775" ]]; then
      echo "::error::$path permissions drifted: ${owner}:${group} mode ${mode}; expected ${expected_owner}:${expected_group} mode 2775. Run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
      return 1
    fi
  done
}

# Bootstrap owns creation and identity. Deploy may converge mode-only legacy
# drift when the directory is already owned by the runner; wrong ownership or a
# missing directory still requires bootstrap. This upgrades old mktemp/rsync -a
# deployments without a one-off host chmod.
laplace_reconcile_app_dir_contract() {
  local app_dir="$1"
  local expected_owner="${2:-laplace-runner}"
  local expected_group="${3:-laplace-runner}"
  local path owner group mode

  for path in "$app_dir" "$app_dir/logs" "$app_dir/mcp-runtime"; do
    if [[ ! -d "$path" ]]; then
      echo "::error::$path missing — run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
      return 1
    fi
    owner="$(stat -c '%U' "$path")"
    group="$(stat -c '%G' "$path")"
    if [[ "$owner" != "$expected_owner" || "$group" != "$expected_group" ]]; then
      echo "::error::$path ownership drifted: ${owner}:${group}; expected ${expected_owner}:${expected_group}. Run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
      return 1
    fi
    mode="$(stat -c '%a' "$path")"
    if [[ "$mode" != "2775" ]]; then
      chmod 2775 "$path" || {
        echo "::error::could not reconcile $path mode ${mode} -> 2775; run bootstrap"
        return 1
      }
      echo "reconciled bootstrap-owned mode: $path ${mode} -> 2775"
    fi
  done

  laplace_require_app_dir_contract "$app_dir" "$expected_owner" "$expected_group"
}
