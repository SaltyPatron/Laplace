#!/usr/bin/env bash

laplace_require_app_dir_contract() {
  local app_dir="$1"
  local owner group mode

  if [[ ! -d "$app_dir" ]]; then
    echo "::error::$app_dir missing — run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
    return 1
  fi

  owner="$(stat -c '%U' "$app_dir")"
  group="$(stat -c '%G' "$app_dir")"
  mode="$(stat -c '%a' "$app_dir")"
  if [[ "$owner" != "laplace-runner" || "$group" != "laplace-runner" || "$mode" != "2775" ]]; then
    echo "::error::$app_dir permissions drifted: ${owner}:${group} mode ${mode}; expected laplace-runner:laplace-runner mode 2775. Run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
    return 1
  fi
}
