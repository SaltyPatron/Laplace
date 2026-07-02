# Archived CI (July 2026) — reference only, not active

Everything in this folder was deleted from the live tree by commit `0b5d1df`
("Manual user commit to clear stage", the doc/comment purge) and recovered here
purely as a design reference for the CI rebuild. GitHub only executes YAML under
`.github/workflows/`, so nothing in this folder runs.

## What lived here

- `workflows/laplace.yml` — the old canonical pipeline. One 360-minute
  self-hosted job ran `scripts/pipeline.sh --mode hot` (build AND deploy AND
  migrate AND publish in a single step) and only then ran ctest / dotnet test /
  pg_regress. Known flaws: deploy-before-test ordering, no per-stage timeouts or
  visibility, hardcoded list of 14 .NET test projects, stale
  `regress_output` diffs dumped on unrelated failures.
- `workflows/_build-deploy.yml`, `workflows/_ingest.yml` — `workflow_call`
  reusables consumed by the per-source wrappers.
- `workflows/<source>.yml` (17 files: wordnet, conceptnet, ud, wiktionary, …) —
  near-identical dispatch wrappers around `_ingest.yml`; parameters (source,
  timeout, consensus gates) duplicated what `scripts/win/witness-manifest.json`
  already encodes.
- `workflows/model-pipeline.yml` — model ingest → consensus gates → GGUF
  synthesis verify. `workflow_call`-only with no caller in the repo, so it was
  unreachable.
- `actions/setup-laplace-env` — oneAPI + LD_LIBRARY_PATH + PG env export.
  Recreated (improved) in the live tree.
- `actions/build-dep` — per-dependency ExternalProject build + artifact verify,
  used by six separate dep jobs. Replaced by a single `deps` job running
  `cmake --build build/deps` (ExternalProject already handles ordering and
  no-ops when current).

## Replaced by

- `.github/workflows/laplace.yml` — sequenced gated jobs (policy/deps → build →
  unit tests → deploy → db ops → integration tests → publish → seed → smoke).
- `.github/workflows/ingest.yml` — one parameterized dispatch for all corpus
  sources.
- `.github/workflows/model.yml` — dispatchable model pipeline.
