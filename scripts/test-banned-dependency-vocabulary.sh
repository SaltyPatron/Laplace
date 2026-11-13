#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

mapfile -d '' -t files_to_scan < <(find . -type f \( -name '*.md' -o -name '*.h' -o -name '*.hpp' -o -name '*.cpp' -o -name '*.c' -o -name '*.cs' -o -name '*.sql' \) \
  ! -path './.git/*' ! -path './node_modules/*' ! -path './.github/archive/*' -print0)
violations=0
for term in HNSWLib hnswlib FAISS ScaNN Milvus Pinecone oneDNN cuDNN; do
  while IFS= read -r match; do
    [[ -z "$match" ]] && continue
    file=${match%%:*}
    case "$file" in
      ./.claude/agents/conventional-ai-skeptic.md) ;;
      *) echo "::warning file=$file::Banned term '$term' appears outside allowed files"; violations=$((violations + 1));;
    esac
  done < <(grep -nH "$term" "${files_to_scan[@]}" 2>/dev/null || true)
done
echo "Total unflagged banned-term occurrences: $violations"
[[ "$violations" -eq 0 ]]
