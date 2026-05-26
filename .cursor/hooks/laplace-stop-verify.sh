#!/usr/bin/env bash
# stop hook: if hot-path trees changed, require verification follow-up.
set -euo pipefail

input=$(cat)
root="${CURSOR_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"
cd "$root" 2>/dev/null || true

# Staged + unstaged paths under Laplace hot trees
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

needs_build=
needs_regress=
needs_verify=
needs_db=

while IFS= read -r f; do
  [[ -z "$f" ]] && continue
  case "$f" in
    engine/*|extension/*) needs_build=1 ;;
    extension/*/sql/*|extension/*/*.sql.in) needs_regress=1 ;;
    app/*) needs_build=1 ;;
    db/migrations/*) needs_db=1 ;;
  esac
done <<<"$changed"

if [[ -z "$needs_build$needs_regress$needs_db" ]]; then
  exit 0
fi

cmds=("just build")
[[ -n "$needs_regress" ]] && cmds+=("just regress")
[[ -n "$needs_db" ]] && cmds+=("just db-up")
# verify is expensive; only nudge when engine/extension touched
if [[ -n "$needs_build" ]]; then
  cmds+=("just verify")
fi

list=$(printf '%s; ' "${cmds[@]}")
list=${list%; }

cat <<EOF
{
  "followup_message": "STOP GATE: Hot-path files changed (${list}). Run these commands now, fix failures, and only then mark the task complete. Report exact pass/fail output in your reply."
}
EOF
exit 0
