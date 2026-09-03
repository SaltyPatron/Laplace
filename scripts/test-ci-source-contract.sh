#!/usr/bin/env bash
# Mechanical CI source-presence contract plus source-only operator contracts that
# must run before build/deployment. Delegated tests here may use temporary roots,
# but may not touch the database, installed runtime, services, or /vault/Data.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

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
  scripts/test-profile-registry.py \
  scripts/test-profiles.json \
  scripts/test-test-profile-registry.py \
  scripts/pr-proof.sh \
  scripts/lib/fp.sh \
  scripts/affected-app.py \
  scripts/setup-host.sh \
  scripts/bootstrap-laplace-runner.sh \
  scripts/ingest-source.sh \
  scripts/dataset-estate-refresh.sh \
  scripts/dataset-estate-refresh.sources.psv \
  scripts/test-dataset-estate-refresh.py \
  scripts/test-dataset-estate-refresh.sh \
  scripts/test-forward-prompt-analysis.py \
  docs/plan/DATASET_ESTATE_REFRESH_OPERATOR.md \
  scripts/ci-policy.sh \
  scripts/ci-policy-suite.sh \
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
  scripts/shellcheck-gate.sh \
  scripts/test-ci-source-contract.sh \
  scripts/test-attestation-law-determinism.sh \
  scripts/test-policy-placement.sh \
  scripts/test-banned-dependency-vocabulary.sh; do
  [[ -f "$f" ]] || { echo "::error file=$f::CI-critical file missing"; fail=1; }
done
[[ "$fail" -eq 0 ]]

# The dataset operator is allowed to manipulate only caller-supplied temporary
# staging roots in policy. Its regression test asserts fail-closed job receipts,
# aggregate verification, bad-artifact preservation, and active-root non-mutation.
bash scripts/test-dataset-estate-refresh.sh

# The dynamic forward pass may optimize duplicate orchestration work, but it may
# not shorten the requested walk or introduce a second route/crawl definition.
python3 scripts/test-forward-prompt-analysis.py
