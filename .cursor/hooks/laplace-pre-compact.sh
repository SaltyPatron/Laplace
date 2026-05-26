#!/usr/bin/env bash
# preCompact: persist working state to .cursor/anchor.md before context compaction.
set -euo pipefail

root="${CURSOR_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
cd "$root" 2>/dev/null || true

mkdir -p .cursor

issue="" mode="implement" subtask=""
if [[ -f .laplace-session ]]; then
  # shellcheck disable=SC1091
  source .laplace-session 2>/dev/null || true
  issue="${ISSUE:-}"
  mode="${MODE:-implement}"
  subtask="${SUBTASK:-}"
fi

branch="$(git branch --show-current 2>/dev/null || echo unknown)"
changed=$(
  {
    git diff --name-only 2>/dev/null
    git diff --name-only --cached 2>/dev/null
    git ls-files --others --exclude-standard 2>/dev/null
  } | sed '/^$/d' | sort -u | head -40
)

{
  echo "# Session anchor (preCompact — read first after compaction)"
  echo
  echo "- **Time (UTC):** $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  echo "- **Branch:** ${branch}"
  echo "- **Mode:** ${mode}"
  echo "- **Issue:** ${issue:-none}"
  if [[ -n "$subtask" ]]; then
    echo "- **Subtask:** ${subtask}"
  fi
  echo
  echo "## Working tree (up to 40 paths)"
  echo
  if [[ -n "$changed" ]]; then
    printf '%s\n' "$changed" | sed 's/^/- /'
  else
    echo "- (clean)"
  fi
  echo
  echo "## Recovery steps"
  echo
  echo "1. Read this file."
  if [[ -n "$issue" ]]; then
    echo "2. \`just anchor ${issue}\` — issue body + prereqs."
    echo "3. Continue the subtask from issue #$issue; do not expand scope silently."
  else
    echo "2. \`just anchor <N>\` — set .laplace-session ISSUE=N if missing."
  fi
  echo "4. Run verification before claiming done (see .cursor/rules/verify-before-done.mdc)."
} > .cursor/anchor.md

exit 0
