# ADR 0013: Two-track CI/CD — hosted for PR validation, self-hosted for integration

> Terminology note: "track" (not "tier") is deliberate. Per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) ratified truth 5, "tier" is reserved exclusively for the Merkle stratum (T0 = Unicode codepoints). This ADR concerns CI/CD pipelines, which are unrelated to the substrate stratum; it uses "track" to avoid colliding with that reserved term. Historical filename retained for link stability.

## Status

**Accepted** — 2026-05-21

## Context

Self-hosted runners on public repos are a security risk: anyone can submit a PR that runs on your machine. The mitigation is **trigger-configuration**: only run on push-to-main (which only the repo owner can do) + workflow_dispatch (manual). This is a two-track split (not a "tier" split — see terminology note above).

PR-validation workflows still need to run on every PR, including from forks — but with untrusted code in disposable VMs, not on the self-hosted runner.

## Decision

Two CI workflows:

1. **`ci.yml`** (GitHub-hosted `ubuntu-latest`, free for public repos): runs on every push + PR + manual. Doc lints, banned-vocabulary scan, link checks. Safe — disposable VM.
2. **`integration.yml`** (self-hosted `hart-server` with labels `laplace,oneapi,postgres-18,...`): runs on push-to-main + workflow_dispatch ONLY. Build, test, integration, smoke tests against real PG. Trusted code only.

## Consequences

- Untrusted code never touches the self-hosted runner.
- PR submitters get rapid feedback from `ci.yml`.
- Self-hosted runner has access to oneAPI, PG18, /vault/models, etc. without exposing those to untrusted code.

## References

- [RULES.md R23 — One-time setup is manual; per-push setup is CI](../../RULES.md) (the R8 reference here was stale: R8 is now "No GPU at runtime or ingest"; R23 governs CI ownership)
- [.github/workflows/integration.yml](../../.github/workflows/integration.yml)
- [.github/workflows/ci.yml](../../.github/workflows/ci.yml)
