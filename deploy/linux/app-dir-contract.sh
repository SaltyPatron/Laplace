#!/usr/bin/env bash

laplace_require_app_dir_contract() {
  local app_dir="$1"
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
    if [[ "$owner" != "laplace-runner" || "$group" != "laplace-runner" || "$mode" != "2775" ]]; then
      echo "::error::$path permissions drifted: ${owner}:${group} mode ${mode}; expected laplace-runner:laplace-runner mode 2775. Run: sudo bash scripts/bootstrap-laplace-runner.sh bootstrap"
      return 1
    fi
  done
}
