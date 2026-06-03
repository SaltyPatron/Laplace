#!/usr/bin/env bash
# scripts/agent-anchor.sh — compact briefing for agents (issue + git + hard stops + prereqs).
# Usage: scripts/agent-anchor.sh [issue_number]
#   Issue defaults to ISSUE= in .laplace-session when omitted.
set -euo pipefail

root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$root"

issue="${1:-}"
mode="implement"
if [[ -z "$issue" && -f .laplace-session ]]; then
  # shellcheck disable=SC1091
  source .laplace-session 2>/dev/null || true
  issue="${ISSUE:-}"
  mode="${MODE:-implement}"
fi

echo "=== Laplace agent anchor ==="
echo "time: $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
echo "repo: $root"
echo "mode: $mode"
if [[ -n "$issue" ]]; then
  echo "issue: #$issue"
else
  echo "issue: (none — set ISSUE= in .laplace-session or: just anchor <N>)"
fi
echo

if [[ -n "$issue" ]]; then
  if command -v gh >/dev/null 2>&1; then
    echo "=== GitHub issue #$issue ==="
    gh issue view "$issue" || echo "(gh issue view failed)"
  else
    echo "=== GitHub issue #$issue ==="
    echo "gh not installed"
  fi
  echo
fi

echo "=== Git ==="
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "branch: $(git branch --show-current 2>/dev/null || echo unknown)"
  echo "recent commits:"
  git log --oneline -5 2>/dev/null || true
  echo
  echo "working tree (first 30 paths):"
  {
    git diff --name-only 2>/dev/null
    git diff --name-only --cached 2>/dev/null
    git ls-files --others --exclude-standard 2>/dev/null
  } | sed '/^$/d' | sort -u | head -30
else
  echo "(not a git repository)"
fi
echo

echo "=== Hard stops (see .cursorrules) ==="
cat <<'EOF'
- No conventional-AI pattern-matching (HNSW/FAISS/RAG/fine-tuning/vector-DB/GEMM hot path)
- Exactly three tables: entities, physicalities, attestations
- Math in C/C++ engine only; PG extension = thin wrappers; C# = orchestration
- float64 coords; Glicko-2 int64 fixed-point; BLAKE3 hash128 — no float32, no libxxhash
- No flat noise thresholds; lottery-ticket sparsity for model sources
- Compiled cascade SRF only — no recursive SQL / cursor / app-loop graph traversal
- Do not edit docs/ARCHITECTURE.md / docs/SUBSTRATE-FOUNDATION.md / docs/INGESTION-STATUS.md / README without explicit user OK
EOF
echo

echo "=== Prereqs (summary) ==="
if [[ -x scripts/check-prereqs.sh ]]; then
  ( scripts/check-prereqs.sh 2>&1 | tail -n 3 ) || true
else
  echo "scripts/check-prereqs.sh missing"
fi
echo

echo "=== Build / verify (support claims — not the deliverable) ==="
echo "  engine/extension/src → just build; extension sql → just regress"
echo "  app / migrations     → just build-app / just db-up as needed"
echo "  integrity claims     → just verify (after substantive hot-path change)"
echo "  docker off           → just test-no-docker"
echo "  Stop hook requires: gap + evidence + next implementation — not verify-only replies"
echo
echo "=== After context compaction ==="
echo "  1. Read .cursor/anchor.md"
echo "  2. Re-run: just anchor ${issue:-<N>}"
