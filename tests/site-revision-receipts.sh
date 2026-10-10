#!/usr/bin/env bash
# The deployed app and web must identify the same build; checkout HEAD is not a receipt.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:?use build scratch}/site-receipts.XXXXXX")
trap 'rm -rf "$scratch"' EXIT
fail() { echo "$*" >&2; exit 1; }
# Exercise the function shipped by site.sh without executing a deployment.
source <(sed -n '/^revision() {/,/^}/p' "$root/deploy/linux/site.sh")
mkdir -p "$scratch/app/wwwroot"
good=1111111111111111111111111111111111111111
other=2222222222222222222222222222222222222222
reject() {
  if (revision "$scratch/app") > "$scratch/out" 2>&1; then
    echo "FAIL $1 was accepted"; exit 1
  fi
  echo "PASS $1 rejected"
}
unset GITHUB_SHA
reject 'missing receipts'
printf '%s\n' "$good" > "$scratch/app/.laplace-source-revision"
reject 'missing web receipt'
printf '%s\n' "$other" > "$scratch/app/wwwroot/.laplace-web-source-revision"
reject 'mixed app and web revisions'
printf 'unknown\n' > "$scratch/app/.laplace-source-revision"
printf 'unknown\n' > "$scratch/app/wwwroot/.laplace-web-source-revision"
reject 'fabricated revision'
printf '%s\n' "$good" > "$scratch/app/.laplace-source-revision"
printf '%s\n' "$good" > "$scratch/app/wwwroot/.laplace-web-source-revision"
GITHUB_SHA=$other reject 'wrong workflow revision'
[[ $(GITHUB_SHA=$good revision "$scratch/app") == "$good" ]]
echo 'PASS matching application, web and workflow revisions'
