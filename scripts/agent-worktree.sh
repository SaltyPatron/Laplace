#!/bin/bash
# agent-worktree.sh <agent-name> [branch] -- give an agent its OWN checkout.
#
# The operator's tree at the repo root stays on main, always. Agents never
# checkout, stash, or switch branches in it.
#
# Why this exists: two agents sharing one working tree is not a merge problem,
# it is a data-loss problem. Uncommitted edits in a shared checkout are
# destroyed by the OTHER agent's `git stash` or `git checkout` -- there is no
# conflict, no warning, and nothing in reflog to recover from, because the
# changes were never committed. Measured on 2026-07-27: a converse_walk.sql.in
# edit (tier filter removed, render batched, scrub deleted) was lost exactly
# this way, and the operator's checkout was repeatedly dragged off main onto
# whichever branch an agent happened to be using.
#
# git worktree already solves it: one .git, N independent checkouts, each on its
# own branch, sharing objects so there is no clone cost.
#
#   scripts/agent-worktree.sh claude              -> .worktrees/claude on a new branch
#   scripts/agent-worktree.sh cursor fix/thing    -> .worktrees/cursor on fix/thing
#
# Removal is `git worktree remove .worktrees/<name>`; `git worktree list` shows
# who holds what.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
name="${1:-}"
branch="${2:-}"

if [[ -z "$name" ]]; then
    echo "usage: $0 <agent-name> [branch]" >&2
    echo "       agents work in .worktrees/<agent-name>; the root tree stays on main" >&2
    exit 2
fi

wt="$ROOT/.worktrees/$name"
branch="${branch:-agent/$name/$(date -u +%Y%m%d-%H%M%S)}"

if [[ -d "$wt" ]]; then
    echo ">>> $name already has a worktree: $wt"
    git -C "$wt" status -sb | head -1
    exit 0
fi

# Always branch from the published main, never from whatever the root tree is on.
git -C "$ROOT" fetch origin --quiet
mkdir -p "$ROOT/.worktrees"

# Shared .git config: the inventory pre-commit hook heals docs/INVENTORY.md so an
# agent cannot leave the shrink-only docs-inventory CI gate red by forgetting to
# regenerate it. Not hypothetical — that gate failed the push of #853 to main and
# held main red until it was found by hand, while this hook already existed.
# core.hooksPath is per-.git, so installing it here covers every worktree.
bash "$ROOT/scripts/install-githooks.sh" >/dev/null

# An EXISTING branch is checked out, never re-created. `worktree add -b <branch>
# ... origin/main` unconditionally minted a new branch at main, so asking for a
# branch that already exists on the remote silently produced a LOCAL branch of the
# same name pointing at main with none of its commits. Nothing failed: the worktree
# came up clean, `git log` showed main, and the first push would have force-diverged
# the real branch and taken its pull request's history with it. Measured 2026-08-04
# against agent/op-array-binding and agent/read-path-volatility-844 — both PR
# branches came up at main, and both would have been overwritten on push.
if git -C "$ROOT" show-ref --verify --quiet "refs/heads/$branch"; then
    from="existing local branch"
    git -C "$ROOT" worktree add "$wt" "$branch"
elif git -C "$ROOT" show-ref --verify --quiet "refs/remotes/origin/$branch"; then
    from="origin/$branch"
    git -C "$ROOT" worktree add --track -b "$branch" "$wt" "origin/$branch"
else
    from="origin/main"
    git -C "$ROOT" worktree add -b "$branch" "$wt" origin/main
fi

echo ">>> $name -> $wt (branch $branch, from $from)"
echo ">>> the root tree is untouched:"
git -C "$ROOT" status -sb | head -1
