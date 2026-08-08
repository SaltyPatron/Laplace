#!/usr/bin/env bash
# GH #847 — shellcheck gate. Severity error only: the gate exists to catch
# swallowed failures and quote bugs that turn into false "all clear", not to
# enforce style across every sourced fragment.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SC_BIN="${SHELLCHECK_BIN:-shellcheck}"

if ! command -v "$SC_BIN" >/dev/null 2>&1; then
  # CI / developer machine without a package: pull the official static binary.
  ver="${SHELLCHECK_VERSION:-v0.10.0}"
  arch="$(uname -m)"
  case "$arch" in
    x86_64|amd64) arch=x86_64 ;;
    aarch64|arm64) arch=aarch64 ;;
    *) echo "::error::unsupported arch for shellcheck binary: $arch"; exit 2 ;;
  esac
  url="https://github.com/koalaman/shellcheck/releases/download/${ver}/shellcheck-${ver}.linux.${arch}.tar.xz"
  tmp="$(mktemp -d)"
  curl -fsSL "$url" -o "$tmp/sc.tar.xz"
  tar -xJf "$tmp/sc.tar.xz" -C "$tmp"
  SC_BIN="$tmp/shellcheck-${ver}/shellcheck"
fi

mapfile -t scripts < <(find "$ROOT/scripts" -type f -name '*.sh' | sort)
if [[ ${#scripts[@]} -eq 0 ]]; then
  echo "::error::no scripts/**/*.sh found"
  exit 2
fi

echo "shellcheck -S error -x (${#scripts[@]} scripts)"
"$SC_BIN" -S error -x "${scripts[@]}"
echo "OK shellcheck gate"
