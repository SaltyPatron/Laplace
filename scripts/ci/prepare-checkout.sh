#!/usr/bin/env bash
# Clean the candidate while retaining independently checked dependency sources
# and web caches whose existing build owner verifies content before reuse.
set -euo pipefail
root=$(git rev-parse --show-toplevel)
[[ ${GITHUB_ACTIONS:-} == true && -n ${GITHUB_WORKSPACE:-} && -n ${GITHUB_SHA:-} ]] || {
  echo 'CI checkout preparation requires an Actions workspace and exact revision' >&2; exit 1;
}
[[ $(realpath -- "$GITHUB_WORKSPACE") == "$root" && $(git rev-parse HEAD) == "$GITHUB_SHA" ]] || {
  echo 'CI checkout does not match the selected workspace/revision' >&2; exit 1;
}
preserved=(
  .ci-dependencies/Laplace-Native
  .ci-dependencies/Laplace-postgres
  .ci-dependencies/Laplace-Engine
  .ci-dependencies/Laplace-Operations
  .ci-dependencies/Laplace-Wiki
  .ci-dependencies/tree-sitter-grammars/tree-sitter-c
  web/node_modules
  web/dist
)
for path in .ci-dependencies .ci-dependencies/tree-sitter-grammars web "${preserved[@]}"; do
  [[ ! -L "$root/$path" ]] || {
    echo "CI cache path may not redirect outside its checkout: $path" >&2; exit 1;
  }
done
exclusions=()
for path in "${preserved[@]}"; do exclusions+=(-e "/$path/"); done
git -C "$root" reset --hard HEAD
git -C "$root" clean -ffdx "${exclusions[@]}" -- .
