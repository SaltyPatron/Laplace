#!/bin/bash
# scripts/migrate-submodule-cache-to-bare.sh
#
# One-time migration of /opt/laplace/external/<name>/ entries from
# working-tree clones (legacy layout written by pre-0d104c7 bootstrap,
# where the gitdir lives at .git/ and the working tree files sit at the
# top) to bare gitdirs at the top level (the layout ci-init-submodules.sh
# expects post-3c8a70f).
#
# Preserves every byte of object data. No upstream re-download. Idempotent:
# entries already in bare-style are detected and skipped.
#
# Per legacy entry:
#   1. mv "$entry"          → "$entry.legacy.$$"   (atomic rename)
#   2. mv "$entry.legacy/.git" → "$entry"          (gitdir lifted to top)
#   3. git config core.bare true                   (mark as bare)
#   4. rm -rf "$entry.legacy.$$"                   (drop working tree)
#
# Step 1 fails → nothing changed. Step 2 fails → rolled back. Step 3 only
# touches the migrated gitdir. Step 4 leaves a residual on failure but the
# migration itself is complete.

set -euo pipefail
cd "$(dirname "$0")/.."

CACHE="${LAPLACE_SUBMODULE_CACHE:-/opt/laplace/external}"

err() { printf 'error: %s\n' "$*" >&2; }
log() { printf '%s\n' "$*"; }

[ -d "$CACHE" ] || { err "no $CACHE"; exit 1; }
[ -w "$CACHE" ] || { err "$CACHE not writable by $(id -un)"; exit 1; }

umask 002

migrate_one() {
    local entry="$1"

    # Already bare-style? Nothing to do.
    if [ -d "$entry/objects" ] && [ -f "$entry/HEAD" ]; then
        return 10
    fi

    # Need a real .git subdirectory to migrate. Gitlink files (workspace
    # clones) are not cache entries; refuse to touch them.
    if [ -L "$entry/.git" ] || [ -f "$entry/.git" ]; then
        err "$entry: .git is a gitlink/file, not a directory — refusing to migrate"
        return 1
    fi
    if [ ! -d "$entry/.git" ]; then
        err "$entry: no .git/ subdirectory — cannot migrate (unknown layout)"
        return 1
    fi

    local backup="${entry}.legacy.$$"

    if ! mv "$entry" "$backup"; then
        err "$entry: rename to $backup failed"
        return 1
    fi

    if ! mv "$backup/.git" "$entry"; then
        err "$entry: lift .git/ to top failed; rolling back"
        mv "$backup" "$entry" \
            || err "$entry: ROLLBACK FAILED — original is at $backup; recover manually"
        return 1
    fi

    git --git-dir="$entry" config core.bare true
    git --git-dir="$entry" config --unset core.worktree 2>/dev/null || true
    git --git-dir="$entry" config --unset core.logallrefupdates 2>/dev/null || true

    # The legacy working-tree leftovers (no .git inside anymore — moved out
    # in step 2) are dead weight. Drop them. Non-fatal if it fails.
    rm -rf "$backup" \
        || err "$entry: residual at $backup — migration succeeded, leftovers can be removed manually"

    # Sanity-check: the migrated entry must be a valid bare gitdir.
    if ! git --git-dir="$entry" rev-parse --is-bare-repository >/dev/null 2>&1; then
        err "$entry: post-migration not a valid bare gitdir"
        return 1
    fi

    return 0
}

converted=0
already=0
failed=0

while IFS= read -r entry; do
    [ -d "$entry" ] || continue

    # Only process directories that look like cache entries: either
    # bare-style (HEAD + objects/) or legacy (has a .git/ subdir).
    # Containers like tree-sitter-grammars/ have neither — skip.
    is_bare=0
    is_legacy=0
    [ -d "$entry/objects" ] && [ -f "$entry/HEAD" ] && is_bare=1
    [ -d "$entry/.git" ] && is_legacy=1
    [ "$is_bare" = 1 ] || [ "$is_legacy" = 1 ] || continue

    if migrate_one "$entry"; then
        converted=$((converted + 1))
        log "  migrated: $entry"
    else
        rc=$?
        case "$rc" in
            10) already=$((already + 1)) ;;
            *)  failed=$((failed + 1)) ;;
        esac
    fi
done < <(find "$CACHE" -mindepth 1 -maxdepth 2 -type d 2>/dev/null | sort)

log ""
log "migrate-submodule-cache: converted=$converted already-bare=$already failed=$failed"
[ "$failed" -eq 0 ]
