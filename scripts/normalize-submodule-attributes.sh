#!/bin/bash
# scripts/normalize-submodule-attributes.sh
#
# Idempotent fix for the upstream `.gitattributes` `* text eol=lf`
# directive that fights intentional CRLF test fixtures in tree-sitter
# grammar submodules. Without this, every fresh `git clone --recursive`
# (or any `git submodule update` that re-checks-out grammar files) leaves
# multiple submodules under `external/tree-sitter-grammars/` reporting
# DIRTY because `test/corpus/*crlf*.txt` (literal CRLF fixtures) get
# normalized to LF in the working tree but the blob holds CRLF.
#
# 5 of 303 tree-sitter grammars are affected at current SHAs (awk, bash,
# c, djot, jsdoc). Auto-detect handles any future additions without
# script edits.
#
# Implementation: enumerate submodule paths from `.gitmodules` directly,
# iterate explicitly. Avoids `git submodule foreach`, which has env-
# dependent recursion behavior (the user's shell vs the agent's shell
# disagree on whether it descends into nested submodules like
# tree-sitter-nqc's broken examples/nqc entry).
#
# The submodules are pinned upstream — we can't modify their
# `.gitattributes`. The fix is a LOCAL-ONLY attribute override
# (`.git/modules/<sm-path>/info/attributes`) saying `<file> -text` for
# each detected CRLF fixture. Local-only = not tracked = doesn't touch
# pristineness.
#
# Idempotent: writes only when the override line is missing; resets only
# when the file is actually dirty. Re-runs on a clean tree are silent.

set -euo pipefail
cd "$(dirname "$0")/.."

[ -f .gitmodules ] || { echo "no .gitmodules at repo root; nothing to do"; exit 0; }

# Parse all submodule paths from .gitmodules (one per line).
mapfile -t sm_paths < <(git config -f .gitmodules --get-regexp '^submodule\..*\.path$' | awk '{print $2}')
if [ "${#sm_paths[@]}" -eq 0 ]; then
    echo "no submodule paths in .gitmodules; nothing to do"
    exit 0
fi

repo_root=$(pwd)
fixes_applied=0
for sm_path in "${sm_paths[@]}"; do
    # Skip submodules whose working tree isn't checked out (avoids errors
    # on partial clones).
    [ -d "$repo_root/$sm_path/.git" ] || [ -f "$repo_root/$sm_path/.git" ] || continue

    # Find CRLF test fixtures under the conventional tree-sitter location.
    # `|| true` so the loop body doesn't trip on submodules without test/.
    matches=$(find "$repo_root/$sm_path/test/corpus" -maxdepth 1 -iname '*crlf*' -type f 2>/dev/null || true)
    [ -z "$matches" ] && continue

    # Resolve the submodule's actual git dir (a file under the parent's
    # .git/modules/...). `git -C` runs there for relative-path output.
    sm_gitdir=$(git -C "$repo_root/$sm_path" rev-parse --absolute-git-dir)
    info_dir="$sm_gitdir/info"
    info_file="$info_dir/attributes"
    mkdir -p "$info_dir"

    while IFS= read -r abs_path; do
        rel="${abs_path#$repo_root/$sm_path/}"
        line="$rel -text"

        # Write override line if missing (idempotent).
        if [ ! -f "$info_file" ] || ! grep -qxF "$line" "$info_file"; then
            printf "%s\n" "$line" >> "$info_file"
            printf "  [+] %s: %s\n" "$sm_path" "$line"
            fixes_applied=$((fixes_applied + 1))
        fi

        # Reset file if currently dirty due to past normalization.
        if ! git -C "$repo_root/$sm_path" diff --quiet -- "$rel" 2>/dev/null; then
            git -C "$repo_root/$sm_path" checkout HEAD -- "$rel"
            printf "  [↺] %s: reset %s\n" "$sm_path" "$rel"
            fixes_applied=$((fixes_applied + 1))
        fi
    done <<< "$matches"
done

if [ "$fixes_applied" -eq 0 ]; then
    echo "✓ submodule attribute overrides clean (no work needed)"
else
    echo "✓ applied $fixes_applied submodule attribute fixes"
fi
