#!/bin/bash

set -euo pipefail
cd "$(dirname "$0")/.."

CACHE="${LAPLACE_SUBMODULE_CACHE:-/opt/laplace/external}"

err()  { printf '::error::%s\n' "$*" >&2; }
warn() { printf '::warning::%s\n' "$*" >&2; }
log()  { printf '%s\n' "$*"; }

[ -f .gitmodules ] || { err "no .gitmodules at $(pwd)"; exit 2; }
[ -d "$CACHE" ]    || { err "no $CACHE — run: sudo scripts/bootstrap-laplace-runner.sh bootstrap"; exit 1; }
[ -w "$CACHE" ]    || { err "$CACHE not writable by $(id -un)"; exit 1; }

umask 002

export GIT_CONFIG_COUNT=1
export GIT_CONFIG_KEY_0='safe.directory'
export GIT_CONFIG_VALUE_0='*'

unbare_in_place() {
    local entry="$1"
    local stash="${entry}.bare.unbare.$$"

    mv "$entry" "$stash" || return 1
    mkdir "$entry"       || { mv "$stash" "$entry"; return 1; }
    mv "$stash" "$entry/.git" || { rmdir "$entry"; mv "$stash" "$entry"; return 1; }

    git --git-dir="$entry/.git" config core.bare false
    git --git-dir="$entry/.git" config --unset core.worktree 2>/dev/null || true
    return 0
}

ensure_pinned() {
    local sm_path="$1" sm_url="$2" pinned="$3"
    local entry="$CACHE/${sm_path#external/}"

    if [ ! -e "$entry" ]; then
        mkdir -p "$(dirname "$entry")"
        if ! git clone --quiet "$sm_url" "$entry"; then
            err "$sm_path: clone failed from $sm_url"
            return 1
        fi
    fi

    if [ -d "$entry/objects" ] && [ -f "$entry/HEAD" ] && [ ! -d "$entry/.git" ]; then
        if ! unbare_in_place "$entry"; then
            err "$sm_path: un-bare conversion failed"
            return 1
        fi
    fi

    if [ ! -d "$entry/.git" ]; then
        err "$sm_path: $entry has no .git/ subdir after un-bare — refusing to touch"
        return 1
    fi

    if ! git -C "$entry" cat-file -e "${pinned}^{commit}" 2>/dev/null; then
        if ! git -C "$entry" fetch --quiet --tags origin '+refs/heads/*:refs/remotes/origin/*'; then
            err "$sm_path: fetch failed; cannot resolve pinned $pinned"
            return 1
        fi
        if ! git -C "$entry" cat-file -e "${pinned}^{commit}" 2>/dev/null; then
            err "$sm_path: pinned $pinned still not present after fetch"
            return 1
        fi
    fi

    current=$(git -C "$entry" rev-parse HEAD 2>/dev/null || echo "")
    if [ "$current" = "$pinned" ]; then
        if git -C "$entry" diff --quiet HEAD 2>/dev/null \
            && [ -n "$(ls "$entry" 2>/dev/null | grep -v '^\.git$' | head -1)" ]; then
            return 0
        fi
    fi

    if ! git -C "$entry" reset --quiet --hard "$pinned"; then
        err "$sm_path: reset --hard $pinned failed"
        return 1
    fi
    return 0
}

# Checked-out HEAD sha of a normal (non-bare, dir-.git) clone WITHOUT spawning
# git — the ~310-entry all-noop sweep must not fork per entry. Prints nothing
# when it cannot resolve (missing entry, bare layout, gitfile, unusual ref);
# callers fall back to `git rev-parse HEAD` for exactly those cases.
checkout_head() {
    local gitdir="$1/.git" head ref sha line
    [ -d "$gitdir" ] || return 0
    IFS= read -r head <"$gitdir/HEAD" 2>/dev/null || return 0
    if [ "${head#ref: }" = "$head" ]; then
        # detached HEAD: the line is the sha itself
        printf '%s' "$head"
        return 0
    fi
    ref="${head#ref: }"
    if [ -f "$gitdir/$ref" ]; then
        IFS= read -r sha <"$gitdir/$ref" 2>/dev/null || return 0
        # loose ref may itself be symbolic in exotic setups — only trust a sha
        case "$sha" in *[!0-9a-f]*|"") return 0 ;; esac
        printf '%s' "$sha"
        return 0
    fi
    if [ -f "$gitdir/packed-refs" ]; then
        while IFS= read -r line; do
            case "$line" in '#'*|'^'*) continue ;; esac
            if [ "${line#* }" = "$ref" ]; then
                printf '%s' "${line%% *}"
                return 0
            fi
        done <"$gitdir/packed-refs"
    fi
    return 0
}

# Non-.git content presence, matching the old `ls | grep -v '^\.git$'` check
# (plain ls skips dotfiles; so does an unset-dotglob glob) — again no fork.
entry_nonempty() {
    local g=("$1"/*)
    [ -e "${g[0]}" ]
}

# Parse .gitmodules once into parallel arrays.
SM_PATHS=(); SM_URLS=()
while IFS=$'\t' read -r sm_path sm_url; do
    [ -n "$sm_path" ] && [ -n "$sm_url" ] || continue
    SM_PATHS+=("$sm_path"); SM_URLS+=("$sm_url")
done < <(
    awk '
        /^\[submodule/ { in_sm=1; path=""; url=""; next }
        in_sm && /^[[:space:]]*path[[:space:]]*=/ { sub(/^[^=]*=[[:space:]]*/,""); path=$0 }
        in_sm && /^[[:space:]]*url[[:space:]]*=/  { sub(/^[^=]*=[[:space:]]*/,""); url=$0 }
        in_sm && path != "" && url != "" { printf "%s\t%s\n", path, url; in_sm=0 }
    ' .gitmodules
)

# ONE `git ls-tree -r` over the tracked prefixes replaces the per-entry
# `git ls-tree HEAD <path>` (~310 forks). -z: no path quoting; gitlinks are
# mode 160000 and -r does not descend into them.
declare -A PINS=()
declare -A _seen_prefix=()
PREFIXES=()
for sm_path in "${SM_PATHS[@]}"; do
    p="${sm_path%%/*}"
    if [ -z "${_seen_prefix[$p]:-}" ]; then
        _seen_prefix[$p]=1
        PREFIXES+=("$p")
    fi
done
if [ "${#PREFIXES[@]}" -gt 0 ]; then
    while IFS= read -r -d '' rec; do
        meta="${rec%%$'\t'*}"
        path="${rec#*$'\t'}"
        [ "${meta%% *}" = "160000" ] || continue
        PINS["$path"]="${meta##* }"
    done < <(git ls-tree -r -z HEAD -- "${PREFIXES[@]}")
fi

total=0; synced=0; nooped=0; failed=0

for i in "${!SM_PATHS[@]}"; do
    sm_path="${SM_PATHS[$i]}"; sm_url="${SM_URLS[$i]}"
    total=$((total + 1))

    pinned="${PINS[$sm_path]:-}"
    if [ -z "$pinned" ]; then
        err "$sm_path: no gitlink at HEAD — submodule declared in .gitmodules but missing from tree"
        failed=$((failed + 1))
        continue
    fi

    entry="$CACHE/${sm_path#external/}"
    before_head=$(checkout_head "$entry")

    # Fast noop path: checkout already at the pin, non-empty, and clean. The
    # dirty check (`diff --quiet HEAD`) is the one remaining per-entry git
    # fork — dropping it would silently stop resetting dirtied cache entries.
    if [ -n "$before_head" ] && [ "$before_head" = "$pinned" ] \
        && entry_nonempty "$entry" \
        && git -C "$entry" diff --quiet HEAD 2>/dev/null; then
        nooped=$((nooped + 1))
    else
        [ -n "$before_head" ] || before_head=$(git -C "$entry" rev-parse HEAD 2>/dev/null || echo "")
        if ensure_pinned "$sm_path" "$sm_url" "$pinned"; then
            after_head=$(git -C "$entry" rev-parse HEAD 2>/dev/null || echo "")
            if [ "$before_head" = "$pinned" ] && [ "$after_head" = "$pinned" ]; then
                nooped=$((nooped + 1))
            else
                synced=$((synced + 1))
                log "  synced: $sm_path → $pinned"
            fi
        else
            failed=$((failed + 1))
        fi
    fi

    if [ $((total % 50)) -eq 0 ]; then
        printf "  [%d] synced=%d nooped=%d failed=%d\n" "$total" "$synced" "$nooped" "$failed"
    fi
done

printf 'sync-external: total=%d synced=%d nooped=%d failed=%d cache=%s\n' \
       "$total" "$synced" "$nooped" "$failed" "$CACHE"
[ "$failed" -eq 0 ]
