#!/usr/bin/env bash
set -euo pipefail

: "${GH_REPO:?GH_REPO is required}"

default=$(gh api "repos/$GH_REPO" --jq .default_branch)
git fetch --prune origin '+refs/heads/*:refs/remotes/origin/*'
base="origin/$default"

removed=0
retained=0
preserved=0

while IFS=' ' read -r ref sha; do
  branch=${ref#refs/remotes/origin/}
  [[ "$branch" != HEAD && "$branch" != "$default" ]] || continue

  safe=${branch//\//-}
  tag="branch-preservation/head-${safe}-${sha:0:12}"
  if ! git ls-remote --exit-code --tags origin "refs/tags/$tag" >/dev/null 2>&1; then
    git tag -a "$tag" "$sha" -m "Preserved branch head before consolidation: $branch @ $sha"
    git push origin "refs/tags/$tag"
    preserved=$((preserved + 1))
  fi

  # Only objective history integration permits deletion. Diverged, rebased,
  # superseded, workflow-only, diagnostic, and otherwise unique heads remain
  # visible until somebody intentionally integrates or resolves them.
  if git merge-base --is-ancestor "$sha" "$base"; then
    git push --force-with-lease="refs/heads/$branch:$sha" origin ":refs/heads/$branch"
    echo "REMOVED merged branch: $branch @ $sha"
    removed=$((removed + 1))
  else
    echo "RETAINED unique branch: $branch @ $sha"
    retained=$((retained + 1))
  fi
done < <(git for-each-ref --format='%(refname) %(objectname)' refs/remotes/origin/)

echo "BRANCH_CONSOLIDATION preserved=$preserved removed=$removed retained=$retained main=$(git rev-parse "$base")"
