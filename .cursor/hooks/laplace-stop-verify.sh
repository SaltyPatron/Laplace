#!/usr/bin/env bash
# stop hook: hot-path edits require accountability — gap evidence + real work,
# NOT a reply that is only "just build / just verify passed".
#
# Laplace is one invention (T0 → attestations → ingest codec → compiled cascade →
# synthesis → behavioral round-trip). This hook blocks validation-theater exits.
set -euo pipefail

root="${CURSOR_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
cd "$root" 2>/dev/null || true

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  exit 0
fi

changed=$(
  {
    git diff --name-only 2>/dev/null
    git diff --name-only --cached 2>/dev/null
    git ls-files --others --exclude-standard 2>/dev/null
  } | sed '/^$/d' | sort -u
)

if [[ -z "$changed" ]]; then
  exit 0
fi

# Hook/meta-only changes: do not force another verify lap.
only_meta=1
invention_touch=
while IFS= read -r f; do
  [[ -z "$f" ]] && continue
  case "$f" in
    .cursor/hooks/*|.cursor/hooks.json|.laplace-session*|docs/adr/*|*.md)
      continue
      ;;
    engine/*|extension/*|app/*|db/migrations/*|Justfile)
      only_meta=
      invention_touch=1
      ;;
    scripts/*)
      # Operational wrappers only — not invention progress; do not force v0.1 gate.
      continue
      ;;
    *)
      only_meta=
      ;;
  esac
done <<<"$changed"

if [[ -z "$invention_touch" && -n "$only_meta" ]]; then
  exit 0
fi

issue="" mode="implement"
if [[ -f .laplace-session ]]; then
  # shellcheck disable=SC1091
  source .laplace-session 2>/dev/null || true
  issue="${ISSUE:-}"
  mode="${MODE:-implement}"
fi

# Summarize touched trees for the agent (first 12 paths).
list=$(echo "$changed" | head -12 | tr '\n' ' ')
list=${list% }

v01="model → substrate → GGUF → chat (#6 #7 #8 #191 #50 per ADR 0037 / milestone v0.1 — model round-trip)"
blockers="cascade SRF + astar (ADR 0035), arena resolver (ADR 0036), roundtrip.sh, doc/issue drift vs code"

msg="ACCOUNTABILITY GATE — hot-path tree dirty (${list}).

Do NOT end with only just build/just verify pass/fail. That is not a deliverable.

Required in your reply (evidence, not narrative):
1. Gap — which part of the invention loop this work targeted (${v01}). Name what is still open (${blockers}) if this did not touch it.
2. Proof — cite paths and/or CI/issue state. No chunk archaeology; ADR 0060: chunks are not sequence.
3. Work — what you implemented or fixed this turn (or state clearly if the turn was wrongly scoped).
4. Next — the single next implementation step you will execute in-repo now, OR one blocking decision for Anthony with tradeoffs (no ticket menus, no \"if you want\").

Verification: run just build (and just regress if extension/sql changed; just db-up if db/migrations changed) only to support claims after substantive code changes — not instead of (1)-(4).
Mode=${mode}${issue:+, issue #${issue}}. Read .cursor/anchor.md; just anchor ${issue:-<N>} if context is thin."

# Escape for JSON string (minimal).
msg_json=$(printf '%s' "$msg" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')

printf '{"followup_message":%s}\n' "$msg_json"
exit 0
