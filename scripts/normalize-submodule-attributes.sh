#!/bin/bash

set -euo pipefail
cd "$(dirname "$0")/.."

[ -f .gitmodules ] || { echo "no .gitmodules at repo root; nothing to do"; exit 0; }

mapfile -t sm_paths < <(git config -f .gitmodules --get-regexp '^submodule\..*\.path$' | awk '{print $2}')
if [ "${#sm_paths[@]}" -eq 0 ]; then
    echo "no submodule paths in .gitmodules; nothing to do"
    exit 0
fi

repo_root=$(pwd)
fixes_applied=0
for sm_path in "${sm_paths[@]}"; do
    [ -d "$repo_root/$sm_path/.git" ] || [ -f "$repo_root/$sm_path/.git" ] || continue

    matches=$(find "$repo_root/$sm_path/test/corpus" -maxdepth 1 -iname '*crlf*' -type f 2>/dev/null || true)
    [ -z "$matches" ] && continue

    sm_gitdir=$(git -C "$repo_root/$sm_path" rev-parse --absolute-git-dir)
    info_dir="$sm_gitdir/info"
    info_file="$info_dir/attributes"
    mkdir -p "$info_dir"

    while IFS= read -r abs_path; do
        rel="${abs_path#$repo_root/$sm_path/}"
        line="$rel -text"

        if [ ! -f "$info_file" ] || ! grep -qxF "$line" "$info_file"; then
            printf "%s\n" "$line" >> "$info_file"
            printf "  [+] %s: %s\n" "$sm_path" "$line"
            fixes_applied=$((fixes_applied + 1))
        fi

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
