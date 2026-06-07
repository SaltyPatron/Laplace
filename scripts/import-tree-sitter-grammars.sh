#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VAULT="${LAPLACE_VAULT_TREESITTER:-/vault/Data/TreeSitter}"
TARGET="$REPO_DIR/external/tree-sitter-grammars"
DRY_RUN=0

for arg in "$@"; do
    case "$arg" in
        --dry-run) DRY_RUN=1 ;;
        --vault)   VAULT="$2"; shift ;;
        --help|-h) echo "Usage: $0 [--dry-run] [--vault PATH]"; exit 0 ;;
        *)         echo "Unknown arg: $arg" >&2; exit 64 ;;
    esac
done

green()  { printf '\033[0;32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[0;33m%s\033[0m\n' "$1"; }
red()    { printf '\033[0;31m%s\033[0m\n' "$1"; }

[ -d "$VAULT" ] || { red "Vault dir not found: $VAULT"; exit 1; }
mkdir -p "$TARGET"

cd "$REPO_DIR"

count=0
added=0
skipped=0
failed=0
LOG="/tmp/tree-sitter-import-errors.log"
: > "$LOG"

for grammar_dir in "$VAULT"/tree-sitter-*; do
    [ -d "$grammar_dir/.git" ] || continue
    name=$(basename "$grammar_dir")
    count=$((count + 1))

    submodule_path="external/tree-sitter-grammars/$name"

    if [ -d "$submodule_path/.git" ] || [ -f "$submodule_path/.git" ]; then
        skipped=$((skipped + 1))
        continue
    fi

    url=$(GIT_DIR="$grammar_dir/.git" git config --get remote.origin.url 2>/dev/null || echo "")
    if [ -z "$url" ]; then
        red "✗ $name: no origin URL in $grammar_dir/.git/config"
        failed=$((failed + 1))
        continue
    fi

    if [ "$DRY_RUN" = 1 ]; then
        echo "git submodule add --reference $grammar_dir $url $submodule_path"
        added=$((added + 1))
        continue
    fi

    if err_output=$(git submodule add "$url" "$submodule_path" 2>&1); then
        added=$((added + 1))
        if [ $((added % 20)) -eq 0 ]; then
            green "  … $added / $count so far"
        fi
    else
        if [ "$failed" -lt 3 ]; then
            red "✗ $name FAILED:"
            echo "    url:  $url"
            echo "    ref:  $grammar_dir"
            echo "    path: $submodule_path"
            echo "    git output:"
            echo "$err_output" | sed 's/^/      /'
        fi
        echo "$name: $err_output" >> "$LOG"
        failed=$((failed + 1))
    fi
done

[ "$failed" -gt 0 ] && yellow "Full failure log: $LOG"

echo
green "Discovered: $count"
green "Added:      $added"
yellow "Skipped:    $skipped (already submodules)"
[ "$failed" -gt 0 ] && red "Failed:     $failed"
green "Done."
