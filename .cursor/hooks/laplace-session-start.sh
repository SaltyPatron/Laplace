#!/usr/bin/env bash
# sessionStart: seed .cursor/anchor.md from .laplace-session; inject brief context.
set -euo pipefail

root="${CURSOR_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
cd "$root" 2>/dev/null || true

mkdir -p .cursor

issue="" mode="implement"
if [[ -f .laplace-session ]]; then
  # shellcheck disable=SC1091
  source .laplace-session 2>/dev/null || true
  issue="${ISSUE:-}"
  mode="${MODE:-implement}"
fi

branch="$(git branch --show-current 2>/dev/null || echo unknown)"
brief="Laplace session: branch=${branch}, mode=${mode}"
if [[ -n "$issue" ]]; then
  brief="${brief}, active issue #${issue}. Run \`just anchor ${issue}\` before coding."
else
  brief="${brief}. Set .laplace-session (see .laplace-session.example) or pass an issue to \`just anchor N\`."
fi

{
  echo "# Session anchor (sessionStart)"
  echo
  echo "- **Time (UTC):** $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  echo "- **Branch:** ${branch}"
  echo "- **Mode:** ${mode}"
  echo "- **Issue:** ${issue:-none}"
  echo
  echo "## After context compaction"
  echo
  echo "1. Read this file."
  if [[ -n "$issue" ]]; then
    echo "2. Run \`just anchor ${issue}\`."
  else
    echo "2. Run \`just anchor <N>\` for the active chunk."
  fi
  echo "3. Re-read acceptance criteria before editing."
} > .cursor/anchor.md

# sessionStart may accept additional_context (Cursor hooks v1)
if command -v jq >/dev/null 2>&1; then
  jq -n --arg ctx "$brief" '{additional_context: $ctx}'
else
  printf '{"additional_context":"%s"}\n' "$(printf '%s' "$brief" | sed 's/\\/\\\\/g;s/"/\\"/g')"
fi
exit 0
