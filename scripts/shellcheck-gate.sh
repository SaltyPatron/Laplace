#!/usr/bin/env bash
# GH #847 — shellcheck gate. The gate exists to catch swallowed failures and
# quote bugs that turn into false "all clear", not to enforce style across every
# sourced fragment.
#
# Raised error -> warning (2026-08-11): SC2155 (`local x=$(cmd)` discards cmd's
# exit status) IS the swallowed-failure class this gate was written for, and it
# only reports at warning. The tree was already clean bar 8 findings, all fixed
# or given a justified disable, so the stricter bar costs nothing to hold.
# Style (info/style severity) remains out of scope.
#
# The linter itself is a SYSTEM DEPENDENCY, installed by
# bootstrap_build_environment alongside every other apt build-dep. This gate
# deliberately does not download, pin, or version-check it: the enforced checks
# are stable across releases
# (0.8.0 and 0.10.0 agree exactly at -S warning on this tree), and a gate that
# fetches its own toolchain is one more thing that can silently drift from what
# the host actually runs. If it is missing, that is a provisioning bug — say so
# and fail, rather than papering over it with a download.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SC_BIN="${SHELLCHECK_BIN:-shellcheck}"

if ! command -v "$SC_BIN" >/dev/null 2>&1; then
  echo "::error::shellcheck not found on PATH — this is a provisioning gap, not a lint failure."
  echo "::error::  sudo apt-get install -y shellcheck"
  echo "::error::A full host provision installs it: sudo bash scripts/setup-host.sh"
  exit 2
fi

mapfile -t scripts < <(find "$ROOT/scripts" -type f -name '*.sh' | sort)
if [[ ${#scripts[@]} -eq 0 ]]; then
  echo "::error::no scripts/**/*.sh found"
  exit 2
fi

echo "$("$SC_BIN" --version | awk '/^version:/{print "shellcheck "$2}') from $(command -v "$SC_BIN")"
echo "shellcheck -S warning -x (${#scripts[@]} scripts)"
"$SC_BIN" -S warning -x "${scripts[@]}"
echo "OK shellcheck gate"
