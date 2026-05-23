#!/bin/bash
# scripts/ci-init-submodules.sh
#
# Mirror-backed submodule init for the self-hosted CI runner.
#
# Why this exists
# ---------------
# The runner workspace at /var/lib/laplace-runner/actions-runner/_work/Laplace/Laplace
# is treated as scratch — actions/checkout can clean it, and we deliberately
# wipe `$WORKSPACE/.git/modules` paths that hold legacy state. None of the
# 300+ submodule clones should live there: re-cloning them on every push
# burns 30+ minutes and saturates upstream. Submodule objects belong in a
# persistent host-side store.
#
# Layout
# ------
# Persistent bare-mirror cache:
#   $LAPLACE_SUBMODULE_CACHE                    (default: /opt/laplace/submodule-cache)
#     <sha256-hex32-of-url>.git/                bare mirror of each submodule URL
#
# Each workspace clone uses `git clone --reference $mirror` so its objects
# are shared (hardlink-on-the-same-fs / read-from-alternates otherwise).
#
# First-run cost  : one full clone of each submodule INTO the mirror.
# Steady-state    : a small `git remote update` delta-fetch into the mirror,
#                   then `git submodule update` fast-forwards the checkout —
#                   typically seconds, regardless of how many submodules.
#
# Wiping the workspace is now safe: re-init pulls from the mirror, not from
# upstream.
#
# Skip list
# ---------
# `tree-sitter-nqc` (NQC = LEGO Mindstorms "Not Quite C") has an upstream
# orphan gitlink at `examples/nqc/` with no `.gitmodules` entry to back it.
# `actions/checkout@v4`'s recursive submodule cleanup walks gitlinks and
# fails with `fatal: No url found for submodule path …/examples/nqc`. We
# never check out nqc, so the orphan never lands in the runner workspace
# and the cleanup walk has nothing to trip on. The single-grammar loss is
# acceptable.
#
# Exit codes
# ----------
#   0   all required submodules initialized (or fall-through init succeeded)
#   1   submodule cache missing AND --strict was requested
#   2   .gitmodules missing (run from repo root)

set -euo pipefail
cd "$(dirname "$0")/.."

CACHE_DIR="${LAPLACE_SUBMODULE_CACHE:-/opt/laplace/submodule-cache}"
STRICT="${LAPLACE_SUBMODULE_STRICT:-0}"
SKIP_PATHS=(
    "external/tree-sitter-grammars/tree-sitter-nqc"
)

# --- helpers -----------------------------------------------------------------
say()  { printf '%s\n' "$*"; }
warn() { printf '::warning::%s\n' "$*" >&2; }
err()  { printf '::error::%s\n'   "$*" >&2; }

is_skipped() {
    local p="$1"
    local s
    for s in "${SKIP_PATHS[@]}"; do
        [ "$p" = "$s" ] && return 0
    done
    return 1
}

# Filesystem-safe mirror key: sha256(url) truncated to 32 hex chars.
mirror_key() {
    printf '%s' "$1" | sha256sum | cut -c1-32
}

# --- preflight ---------------------------------------------------------------
[ -f .gitmodules ] || { err ".gitmodules not found at $(pwd)"; exit 2; }

CACHE_AVAILABLE=1
if [ ! -d "$CACHE_DIR" ]; then
    if [ "$STRICT" = "1" ]; then
        err "Submodule cache missing at $CACHE_DIR — run: sudo scripts/bootstrap-laplace-runner.sh"
        exit 1
    fi
    warn "Submodule cache missing at $CACHE_DIR — falling back to direct upstream init (no caching)"
    warn "Speed-fix once the cache directory exists; objects will start flowing through the mirror automatically"
    CACHE_AVAILABLE=0
fi

# Parse submodule (name, path, url) triples from .gitmodules
mapfile -t names < <(
    git config -f .gitmodules --get-regexp '^submodule\..*\.path$' \
    | awk '{print $1}' \
    | sed 's/^submodule\.//; s/\.path$//'
)
[ "${#names[@]}" -gt 0 ] || { warn "no submodules declared in .gitmodules; nothing to init"; exit 0; }

inited=0
skipped=0
fresh_mirrors=0
errors=0

# --- main --------------------------------------------------------------------
for name in "${names[@]}"; do
    path=$(git config -f .gitmodules "submodule.${name}.path")
    url=$(git config -f .gitmodules "submodule.${name}.url")

    if is_skipped "$path"; then
        # Make sure no stale checkout / module dir lingers — actions/checkout's
        # cleanup walks anything it finds in .git/modules or the working tree.
        rm -rf -- ".git/modules/$path" "$path" 2>/dev/null || true
        skipped=$((skipped + 1))
        continue
    fi

    # Pinned SHA the parent expects for this submodule. `git submodule update`
    # checks out this SHA but does NOT fetch by default — if the runner's local
    # clone of the submodule is stale (older than a pin bump), the checkout
    # fails with "Unable to find current revision". We pre-fetch the SHA below.
    pinned=$(git ls-tree HEAD "$path" 2>/dev/null | awk '{print $3}')

    if [ "$CACHE_AVAILABLE" = "1" ]; then
        key=$(mirror_key "$url")
        mirror="$CACHE_DIR/${key}.git"

        if [ -d "$mirror" ]; then
            # Delta-fetch into the mirror. With --mirror the refspec is
            # `+refs/*:refs/*`, so this pulls heads, tags, and pull refs —
            # any pinned SHA reachable from upstream lands here.
            git -C "$mirror" remote update --prune >/dev/null 2>&1 \
                || warn "mirror fetch failed for $name (continuing with cached state)"
        else
            say "  [mirror] cloning $url → $mirror"
            if ! git clone --mirror --quiet "$url" "$mirror"; then
                err "failed to create mirror at $mirror for $name"
                errors=$((errors + 1))
                continue
            fi
            fresh_mirrors=$((fresh_mirrors + 1))
        fi

        # Ensure the workspace module dir borrows objects from the mirror
        # BEFORE we try to check out the pinned SHA. Two cases:
        #   * fresh init  → `--reference` plants the alternates on clone
        #   * existing    → we write alternates ourselves so the next op
        #                   resolves the pinned SHA via the mirror's objects
        if [ ! -e ".git/modules/$path" ]; then
            init_status=0
            git submodule update --init --reference "$mirror" -- "$path" || init_status=$?
        else
            mkdir -p ".git/modules/$path/objects/info"
            printf '%s\n' "$mirror/objects" > ".git/modules/$path/objects/info/alternates"
            init_status=0
            git submodule update --init -- "$path" || init_status=$?
        fi
    else
        # No cache — direct upstream init.
        init_status=0
        git submodule update --init -- "$path" || init_status=$?
    fi

    # Recovery: a non-zero exit from `git submodule update` is almost always
    # "Unable to find current revision" caused by stale local state that
    # predates a pin bump (the script's most common runtime failure). Fetch
    # the pinned SHA explicitly into whatever clone exists, then retry.
    # If that still fails, wipe the local state and re-clone fresh.
    if [ "$init_status" -ne 0 ]; then
        mod_git=".git/modules/$path"
        if [ -d "$mod_git" ] && [ -n "$pinned" ]; then
            git --git-dir="$mod_git" fetch --quiet --tags origin "+refs/heads/*:refs/remotes/origin/*" 2>/dev/null \
                || git --git-dir="$mod_git" fetch --quiet origin "$pinned" 2>/dev/null \
                || true
            init_status=0
            git submodule update --init --force -- "$path" || init_status=$?
        fi
    fi
    if [ "$init_status" -ne 0 ]; then
        warn "$path: stale local clone (pinned $pinned unreachable); wiping and re-cloning"
        rm -rf -- ".git/modules/$path" "$path"
        if [ "$CACHE_AVAILABLE" = "1" ] && [ -n "${mirror:-}" ] && [ -d "$mirror" ]; then
            git submodule update --init --reference "$mirror" -- "$path" \
                || { err "submodule init failed after wipe: $path"; errors=$((errors + 1)); continue; }
        else
            git submodule update --init -- "$path" \
                || { err "submodule init failed after wipe: $path"; errors=$((errors + 1)); continue; }
        fi
    fi

    inited=$((inited + 1))
done

# CRLF-fixture overrides for the 5 tree-sitter grammars that fight git's
# default text=lf normalization. Idempotent; silent when there's nothing
# to fix.
scripts/normalize-submodule-attributes.sh

say "ci-init-submodules: inited=$inited skipped=$skipped fresh-mirrors=$fresh_mirrors errors=$errors cache=$CACHE_DIR"
[ "$errors" -eq 0 ]
