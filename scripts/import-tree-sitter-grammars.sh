#!/bin/bash
# scripts/import-tree-sitter-grammars.sh
#
# Bulk-import 303 tree-sitter grammar repos as git submodules under
# external/tree-sitter-grammars/<lang>/. Reads each grammar's upstream
# URL from /vault/Data/TreeSitter/<lang>/.git/config (origin remote).
# (all-deps-as-submodules).
#
# Usage:
#   scripts/import-tree-sitter-grammars.sh                  # do the work
#   scripts/import-tree-sitter-grammars.sh --dry-run        # print what would happen
#   scripts/import-tree-sitter-grammars.sh --vault PATH     # override /vault path
#
# Strategy:
#   - For each tree-sitter-* dir under VAULT, extract its origin URL
#   - git submodule add <url> external/tree-sitter-grammars/<lang>
#   - Fresh clone from upstream (not --reference — /vault clones are shallow,
#     git refuses shallow refs as a reference backstop)
#   - Skips grammars already added
#
# Output: 303 submodule entries in .gitmodules. Total init time on fresh
# checkout ~5-10 min. Per-grammar init is opt-in: `git submodule update
# --init external/tree-sitter-grammars/tree-sitter-python`.

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
        --help|-h) sed -n '2,/^$/p' "$0"; exit 0 ;;
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

    # Clone fresh from upstream — `--reference <local-shallow>` doesn't work
    # because /vault clones are shallow (git refuses shallow refs as
    # backstop for full history). Each grammar is small (a few MB); 303
    # of them takes ~5-15 min one-time.: tree-sitter grammars
    # are C source deps that compile to .so parsers via tree-sitter CLI;
    # the same submodule policy applies as for PostgreSQL/BLAKE3/etc.
    # `if` consumes the exit code so `set -e` doesn't kill us on first failure.
    if err_output=$(git submodule add "$url" "$submodule_path" 2>&1); then
        added=$((added + 1))
        if [ $((added % 20)) -eq 0 ]; then
            green "  … $added / $count so far"
        fi
    else
        # Show the first 3 failures in full so we can debug systemic issues.
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
