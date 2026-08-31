#!/usr/bin/env bash
# Deterministic source/policy proof for the primary pipeline.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

section() { echo "::group::$1"; }
endsection() { echo "::endgroup::"; }

section "CI source contract"
fail=0
for f in \
  .github/workflows/laplace.yml \
  .github/workflows/pr-validation.yml \
  .github/workflows/db-ops.yml \
  .github/workflows/repo-hygiene.yml \
  .github/workflows/_ingest.yml \
  .github/workflows/seed-foundation.yml \
  .github/workflows/seed-knowledge.yml \
  .github/workflows/seed-documents.yml \
  .github/workflows/seed-chess.yml \
  .github/workflows/seed-code.yml \
  .github/workflows/seed-models.yml \
  .github/actions/setup-laplace-env/action.yml \
  scripts/pipeline.sh \
  scripts/test-parallel.sh \
  scripts/pr-proof.sh \
  scripts/lib/fp.sh \
  scripts/affected-app.py \
  scripts/setup-host.sh \
  scripts/bootstrap-laplace-runner.sh \
  scripts/ingest-source.sh \
  scripts/ci-policy.sh \
  scripts/ci-deps.sh \
  scripts/actions-audit.py \
  scripts/isa-gate-check.py \
  scripts/isa-gate-baseline.json \
  scripts/model-payload-gate-check.py \
  scripts/model-payload-gate-baseline.json \
  scripts/ensure-foundation.sh \
  scripts/laplace_api.py \
  scripts/eval-generation.py \
  scripts/verify-generation.py \
  scripts/test-eval-op-lane.py \
  scripts/test-actions-topology.py \
  scripts/test-application-publish.py \
  scripts/test-application-runtime.py \
  scripts/publish-applications.sh \
  scripts/check-application-runtime.py \
  scripts/verify-application-release.py \
  scripts/eval-probes.json \
  scripts/eval-baselines.json \
  scripts/shellcheck-gate.sh; do
  [[ -f "$f" ]] || { echo "::error file=$f::CI-critical file missing"; fail=1; }
done
[[ "$fail" -eq 0 ]]
python3 scripts/test-actions-topology.py
python3 scripts/actions-audit.py
endsection

section "Shell and deploy contracts"
bash scripts/shellcheck-gate.sh
bash scripts/test-deploy-payload-sync.sh
python3 scripts/test-pipeline-install.py
python3 scripts/test-application-runtime.py
python3 scripts/test-stockfish-release.py
python3 scripts/test-managed-services.py
python3 scripts/test-managed-host.py
python3 scripts/test-managed-tls.py
python3 scripts/test-pg-access.py
shellcheck -S warning -x deploy/linux/deploy.sh deploy/linux/managed-publish.sh
endsection

section "Manifest, SQL and ISA policy"
python3 scripts/validate-pipeline.py
python3 scripts/test-eval-op-lane.py
python3 scripts/test-sql-audit.py
python3 scripts/sql-audit.py --skip-near-clones --fail-on high
python3 scripts/check-upgrade-drop-order.py
python3 scripts/isa-gate-check.py
python3 scripts/model-payload-gate-check.py
endsection

section "Attestation law determinism"
outs=(
  engine/core/src/generated/relation_law.c
  engine/core/src/generated/pos_law.c
  engine/core/include/laplace/core/relation_law.h
  engine/core/include/laplace/core/pos_law.h
  engine/core/include/laplace/core/highway_manifest.h
  extension/laplace_substrate/sql/generated/seed_relation_types.sql.in
  extension/laplace_substrate/sql/generated/seed_pos.sql.in
)
python3 scripts/codegen-attestation-law.py
for f in "${outs[@]}"; do
  [[ -f "$f" ]] || { echo "::error file=$f::codegen did not produce a declared output"; exit 1; }
done
a=$(cat "${outs[@]}" | sha256sum | cut -d' ' -f1)
rm -f "${outs[@]}"
python3 scripts/codegen-attestation-law.py
b=$(cat "${outs[@]}" | sha256sum | cut -d' ' -f1)
[[ "$a" == "$b" ]] || { echo "::error::codegen non-deterministic ($a != $b)"; exit 1; }
echo "codegen deterministic across a clean regen: $a"
endsection

section "Documentation and policy placement"
python3 scripts/docs-inventory.py --check
mapfile -d '' -t policy_files < <(find app \( -path 'app/Laplace.Decomposers/*.cs' -o -path 'app/Laplace.Decomposers/*/*.cs' -o -path 'app/Laplace.Substrate/Abstractions/*.cs' \) \
  ! -path 'app/Laplace.Decomposers.Tests/*' -print0)
violations=0
for pattern in AttestationFactory 'RelationTypeRegistry\.Attest' 'ScoreFp1e9\s*=' 'AttestationOutcome\.(Confirm|Refute|Draw)'; do
  while IFS= read -r file; do
    [[ -z "$file" ]] && continue
    echo "::error file=$file::Forbidden attestation policy: $pattern"
    violations=$((violations + 1))
  done < <(rg -l "$pattern" "${policy_files[@]}" 2>/dev/null || true)
done
[[ "$violations" -eq 0 ]]
endsection

section "Banned dependency vocabulary"
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
endsection
