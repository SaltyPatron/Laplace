#!/bin/bash
# scripts/ci-init-submodules.sh
#
# Submodule init for self-hosted CI — fast path via the persistent
# canonical clone tree at /opt/laplace/external/, populated and
# refreshed by bootstrap-laplace-runner.sh's bootstrap_submodule_cache
# step. The runner work folder stays scratch; this script never
# re-downloads from upstream — it only points workspace submodule
# inits at the cached clones via git's --reference / alternates.
#
# Cache layout: /opt/laplace/external/<submodule-path>/  (full clone
# with .git/ inside). $ref_dir = $CACHE/$path/.git is the gitdir we
# hand to `git submodule update --reference`.
#
# Why we don't symlink .git/modules into the cache: each submodule's
# .git/modules/<sm>/config records core.worktree as a RELATIVE path
# (e.g. ../../../../external/postgresql). That path resolves through
# the workspace's real .git/modules layout — through a symlink to a
# different parent, it resolves to the wrong place and `git submodule
# update` aborts with "cannot chdir to ../../../../external/<sm>".
#
# Instead: workspace .git/modules is a real directory. Each per-submodule
# gitdir borrows objects from the cache via git's --reference / alternates
# mechanism. The cache provides the bytes; the workspace gitdir provides
# the per-workspace refs + config (including a correct core.worktree).
#
# Per submodule the script:
#   1. If the cache's copy lacks the pinned SHA, fetch heads + tags into
#      the cache (one network op per actually-bumped submodule).
#   2. If workspace .git/modules/<sm> doesn't exist: `git submodule update
#      --init --reference <cache>` — fresh workspace gitdir, alternates
#      pointing at cache, no upstream re-download.
#   3. If workspace .git/modules/<sm> exists: write alternates to point at
#      cache, then `git submodule update --init --force`.
#   4. Verify post-init HEAD matches pinned SHA; mismatch is a hard error.
#
# Skip list: external/tree-sitter-grammars/tree-sitter-nqc (upstream
# orphan gitlink at examples/nqc trips actions/checkout cleanup).

set -euo pipefail
cd "$(dirname "$0")/.."

CACHE="${LAPLACE_SUBMODULE_CACHE:-/opt/laplace/external}"
SKIP=("external/tree-sitter-grammars/tree-sitter-nqc")

err() { printf '::error::%s\n' "$*" >&2; }

[ -f .gitmodules ] || { err "no .gitmodules at $(pwd)"; exit 2; }

if [ ! -d "$CACHE" ]; then
    err "submodule cache $CACHE does not exist — run: sudo scripts/bootstrap-laplace-runner.sh bootstrap"
    exit 1
fi

is_skip() {
    local p="$1" s
    for s in "${SKIP[@]}"; do [ "$p" = "$s" ] && return 0; done
    return 1
}

mapfile -t names < <(
    git config -f .gitmodules --get-regexp '^submodule\..*\.path$' \
    | awk '{print $1}' | sed 's/^submodule\.//; s/\.path$//'
)

ok=0; skipped=0; fail=0
for name in "${names[@]}"; do
    path=$(git config -f .gitmodules "submodule.${name}.path")

    if is_skip "$path"; then
        rm -rf -- ".git/modules/$path" "$path"
        skipped=$((skipped + 1))
        continue
    fi

    pinned=$(git ls-tree HEAD "$path" 2>/dev/null | awk '{print $3}')
    if [ -z "$pinned" ]; then
        err "$path: no gitlink at HEAD"
        fail=$((fail + 1))
        continue
    fi

    # Cache entries are full clones — point at their .git dir.
    ref_dir="$CACHE/$path/.git"
    ws_mod=".git/modules/$path"

    # Fetch into cache if it's behind the pinned SHA.
    if [ -d "$ref_dir" ] && ! git --git-dir="$ref_dir" cat-file -e "${pinned}^{commit}" 2>/dev/null; then
        git --git-dir="$ref_dir" fetch --quiet --tags origin \
            '+refs/heads/*:refs/remotes/origin/*' >/dev/null 2>&1 || true
    fi

    if [ ! -d "$ws_mod" ] && [ -d "$ref_dir" ]; then
        git submodule update --init --reference "$ref_dir" -- "$path" || {
            err "$path: init failed (pinned $pinned)"
            fail=$((fail + 1))
            continue
        }
    elif [ -d "$ws_mod" ] && [ -d "$ref_dir" ]; then
        mkdir -p "$ws_mod/objects/info"
        printf '%s\n' "$ref_dir/objects" > "$ws_mod/objects/info/alternates"
        git submodule update --init --force -- "$path" || {
            err "$path: update failed (pinned $pinned)"
            fail=$((fail + 1))
            continue
        }
    else
        err "$path: cache miss at $ref_dir — bootstrap_submodule_cache should have created it"
        fail=$((fail + 1))
        continue
    fi

    actual=$(git -C "$path" rev-parse HEAD 2>/dev/null || true)
    if [ "$actual" != "$pinned" ]; then
        err "$path: post-init HEAD=$actual pinned=$pinned"
        fail=$((fail + 1))
        continue
    fi
    ok=$((ok + 1))
done

scripts/normalize-submodule-attributes.sh

printf 'ci-init-submodules: ok=%d skipped=%d fail=%d total=%d cache=%s\n' \
       "$ok" "$skipped" "$fail" "${#names[@]}" "$CACHE"
[ "$fail" -eq 0 ]
