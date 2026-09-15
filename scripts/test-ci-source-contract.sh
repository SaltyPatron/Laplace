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
  scripts/test-app-fingerprints.py \
  scripts/setup-host.sh \
  scripts/bootstrap-laplace-runner.sh \
  scripts/ingest-source.sh \
  scripts/verify-operational-seed.py \
  scripts/test-operational-seed.py \
  scripts/dataset-estate-refresh.sh \
  scripts/dataset-estate-refresh.sources.psv \
  scripts/test-dataset-estate-refresh.py \
  scripts/test-dataset-estate-refresh.sh \
  scripts/test-forward-prompt-analysis.py \
  scripts/test-upgrade-drop-order.py \
  scripts/test-installed-extension-current.py \
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
  scripts/check-substrate-floor.sh \
  scripts/prove-live-recursive-substrate.py \
  scripts/test-live-recursive-proof-gate.py \
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

# The live recursive proof runs only on the seeded/shared profile, but its SQL
# construction and floor wiring are source contracts and must fail before build if
# an edit starts measuring packed carrier coordinates or drops a hard invariant.
python3 scripts/test-live-recursive-proof-gate.py

# Deployment must read back this invocation's complete authored operational seed.
python3 scripts/test-operational-seed.py

# BEGIN ATOMIC pg_depend release is part of live extension-upgrade safety. Prove
# both legal release forms (drop/rebind) and the unsafe rebind/ordering cases with
# a synthetic manifest before the live-catalog checker uses that model.
python3 scripts/test-upgrade-drop-order.py
python3 scripts/test-sql-manifest-dependencies.py

# EXT_VERSION includes the content-versioned execution module. The installed/source
# parity checker must therefore hash the same configured execution identity as CMake.
python3 scripts/test-installed-extension-current.py

# Ingest interruption is diagnostic metadata, never successful completion.
python3 scripts/test-ingest-source-exit.py

# An external seeded source edit must invalidate its real project consumers,
# their tests, the ingest CLI build, and the application publish domain.
python3 scripts/test-app-fingerprints.py
