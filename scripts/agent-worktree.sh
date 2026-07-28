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
git -C "$ROOT" fetch origin main --quiet
mkdir -p "$ROOT/.worktrees"
git -C "$ROOT" worktree add -b "$branch" "$wt" origin/main

echo ">>> $name -> $wt (branch $branch, from origin/main)"
echo ">>> the root tree is untouched:"
git -C "$ROOT" status -sb | head -1
