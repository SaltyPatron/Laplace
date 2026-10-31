#!/usr/bin/env bash
# Clean the candidate while retaining independently checked dependency sources
# and caches whose existing build owners verify content before reuse.
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
# Configure runs the law generator before consuming these ignored outputs.
# Its write-if-changed owner checks every byte against the current manifests.
generated=()
for law in relation pos entity_type language deprel qualifier trust_class firmware; do
  generated+=("engine/core/include/laplace/core/${law}_law.h" "engine/core/src/generated/${law}_law.c")
done
generated+=(engine/core/include/laplace/core/highway_manifest.h)
for path in "${preserved[@]}" "${generated[@]}"; do
  ancestor=$path
  while :; do
    [[ ! -L "$root/$ancestor" ]] || {
      echo "CI cache path may not redirect outside its checkout: $ancestor" >&2; exit 1;
    }
    [[ $ancestor == */* ]] || break
    ancestor=${ancestor%/*}
  done
done
for path in "${generated[@]}"; do
  [[ ! -e "$root/$path" || -f "$root/$path" ]] || {
    echo "Generated CI input must be a regular file: $path" >&2; exit 1;
  }
done
exclusions=()
for path in "${preserved[@]}"; do exclusions+=(-e "/$path/"); done
for path in "${generated[@]}"; do exclusions+=(-e "/$path"); done
git -C "$root" reset --hard HEAD
git -C "$root" clean -ffdx "${exclusions[@]}" -- .
