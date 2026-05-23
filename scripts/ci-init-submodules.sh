#!/bin/bash
# scripts/ci-init-submodules.sh
#
# Submodule init + cache management for the Laplace pipeline (and for
# developers running this directly via group membership — no sudo).
#
# Two responsibilities, both idempotent:
#
#   1. Ensure the persistent cache at /opt/laplace/external/<name>/ has a
#      full clone of every submodule in .gitmodules. Missing entries are
#      cloned from upstream once; existing entries are fetched (cheap if
#      already at the pinned SHA). Bootstrap creates the empty cache dir
#      with the right group + setgid; this script populates it. New
#      submodules added to .gitmodules get picked up on the next run.
#
#   2. Initialize the workspace's .git/modules/<path> using the cache as
#      a git --reference / alternates source — objects come from the
#      cache, nothing re-downloads from upstream. Workspace clones are
#      thin pointers backed by the cache's object store.
#
# Cache layout: /opt/laplace/external/<submodule-name>/ (the in-repo
# "external/" path prefix is stripped — repo's external/postgresql lives
# at /opt/laplace/external/postgresql). $ref_dir = $CACHE/<name>/.git.
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

err()  { printf '::error::%s\n' "$*" >&2; }
warn() { printf '::warning::%s\n' "$*" >&2; }
log()  { printf '%s\n' "$*"; }

[ -f .gitmodules ] || { err "no .gitmodules at $(pwd)"; exit 2; }

if [ ! -d "$CACHE" ]; then
    err "$CACHE does not exist — run: sudo scripts/bootstrap-laplace-runner.sh bootstrap"
    err "(bootstrap creates the empty cache directory with the right perms;"
    err " this script populates it from .gitmodules.)"
    exit 1
fi
if [ ! -w "$CACHE" ]; then
    err "$CACHE not writable by $(id -un) — confirm you are in the laplace-runner group"
    exit 1
fi

# Files we create in the cache need to be group-writable so ahart and
# laplace-runner can both update entries. setgid on the parent inherits
# the group; umask 002 ensures files come out 664 / dirs 2775.
umask 002

# ---------------------------------------------------------------------------
# Phase A — populate cache: for every submodule in .gitmodules, ensure
# /opt/laplace/external/<name>/ has a full clone. Missing → clone once.
# Present → fetch (cheap when already at the pinned SHA).
# ---------------------------------------------------------------------------
log "Phase A: ensure cache /opt/laplace/external/ is populated from .gitmodules"

cache_total=$(grep -c '^\[submodule' .gitmodules)
cache_cloned=0; cache_fetched=0; cache_failed=0; cache_seen=0; cache_last_log=0

while IFS=$'\t' read -r sm_path sm_url; do
    [ -n "$sm_path" ] && [ -n "$sm_url" ] || continue
    cache_seen=$((cache_seen + 1))

    # Skip the known-bad nqc grammar (orphan gitlink).
    skip_this=0
    for s in "${SKIP[@]}"; do [ "$sm_path" = "$s" ] && skip_this=1 && break; done
    [ "$skip_this" = 1 ] && continue

    rel="${sm_path#external/}"     # /opt/laplace/external/<name>, not /external/external/
    cache_entry="$CACHE/$rel"

    if [ -d "$cache_entry/.git" ]; then
        if git -C "$cache_entry" fetch --quiet --tags \
            origin '+refs/heads/*:refs/remotes/origin/*' 2>/dev/null; then
            cache_fetched=$((cache_fetched + 1))
        else
            cache_failed=$((cache_failed + 1))
            warn "cache fetch failed: $sm_path"
        fi
    else
        mkdir -p "$(dirname "$cache_entry")"
        if git clone --quiet "$sm_url" "$cache_entry" 2>/dev/null; then
            cache_cloned=$((cache_cloned + 1))
        else
            cache_failed=$((cache_failed + 1))
            warn "cache clone failed: $sm_path ($sm_url)"
        fi
    fi

    if [ $((cache_seen - cache_last_log)) -ge 25 ]; then
        printf "  [%d/%d] cloned=%d fetched=%d failed=%d\n" \
            "$cache_seen" "$cache_total" "$cache_cloned" "$cache_fetched" "$cache_failed"
        cache_last_log=$cache_seen
    fi
done < <(
    awk '
        /^\[submodule/ { in_sm=1; path=""; url=""; next }
        in_sm && /^[[:space:]]*path[[:space:]]*=/ { sub(/^[^=]*=[[:space:]]*/,""); path=$0 }
        in_sm && /^[[:space:]]*url[[:space:]]*=/  { sub(/^[^=]*=[[:space:]]*/,""); url=$0 }
        in_sm && path != "" && url != "" { printf "%s\t%s\n", path, url; in_sm=0 }
    ' .gitmodules
)

log "  cache summary: cloned=$cache_cloned fetched=$cache_fetched failed=$cache_failed total=$cache_total"

# ---------------------------------------------------------------------------
# Phase B — init workspace submodules using the cache as --reference.
# Workspace gets thin clones; objects come from /opt/laplace/external.
# ---------------------------------------------------------------------------
log ""
log "Phase B: init workspace submodules from cache"

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

    # Cache entries are full clones at $CACHE/<name>/ — strip the
    # in-repo "external/" prefix from $path to find the right entry
    # (cache layout is /opt/laplace/external/<name>/, not
    # /opt/laplace/external/external/<name>/).
    ref_dir="$CACHE/${path#external/}/.git"
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
