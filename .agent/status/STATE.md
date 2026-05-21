# Laplace — Project State

**Last updated:** 2026-05-21 (Chunk 0 complete — skeleton builds + CI verifies)
**Updated by:** claude (Chunk 0 execution)

---

## Current phase

**Chunk 0 — Plan + deps + skeleton.** ✅ COMPLETE

All acceptance criteria green:
- ✅ `just build` succeeds (engine + extension + app)
- ✅ Engine produces `liblaplace_engine.so.0.1.0` with proper soname symlinks; ctest passes
- ✅ PG extension produces `laplace.so` via PGXS
- ✅ C# app produces `Laplace.Engine.dll` (net10.0)
- ✅ `integration.yml` build job passes on `hart-server` self-hosted runner
- ✅ `ci.yml` (GitHub-hosted) passes for doc/lint/banned-vocab checks
- ✅ GitHub Issues #1-#8 created for Chunks 1-8

Framework documentation, rules, glossary, standards, design spec, operations manual, agent definitions, and status-tracking infrastructure all in place. GitHub Actions CI/CD active (hosted PR-validation + self-hosted integration on `hart-server`).

## Repository

- **GitHub:** https://github.com/SaltyPatron/Laplace (public)
- **Default branch:** `main`
- **Commits:** `b7614f2` (initial .gitignore) → `fb46bb3` (framework foundation) → `b68322a` (CI/CD workflows)

## Runner

- **Name:** `hart-server`
- **Service:** `actions.runner.SaltyPatron-Laplace.hart-server.service` (systemd, enabled, running)
- **Labels:** `self-hosted, Linux, X64, laplace, oneapi, postgres-18, dotnet-10, avx2`
- **Capabilities verified:** Intel oneAPI 2026.0.0, PostgreSQL 18.3, PostGIS 3.6.3, .NET 10.0.107, Eigen 3.4.0, ICU 70.1, libxxhash 0.8.1
- **Security posture:** push-to-main + workflow_dispatch ONLY on self-hosted; never pull_request from forks.

## Workflows

- **CI (GitHub-hosted):** `.github/workflows/ci.yml` — required-files check, markdown lint, link check, banned-vocabulary scan. Triggers on push, PR, manual.
- **Integration (self-hosted):** `.github/workflows/integration.yml` — capability check now; expands as code lands. Triggers on push to main + manual ONLY.

## Milestones

| # | Milestone | Status | Verified by |
|---|---|---|---|
| 0 | Plan + deps + skeleton | ✅ DONE | commit 1f11511, CI green |
| 1 | Core math primitives (coord4d, hash128, hilbert4d, mantissa) | ⏳ NEXT | [#1](https://github.com/SaltyPatron/Laplace/issues/1) |
| 2 | Geometry serde + GIST integration | — | [#2](https://github.com/SaltyPatron/Laplace/issues/2) |
| 3 | Perf-cache + T0 seed (Unicode UCD) | — | [#3](https://github.com/SaltyPatron/Laplace/issues/3) |
| 4 | First linguistic source (WordNet) | — | [#4](https://github.com/SaltyPatron/Laplace/issues/4) |
| 5 | Glicko-2 + cross-source dynamics | — | [#5](https://github.com/SaltyPatron/Laplace/issues/5) |
| 6 | TransformerModelSource + Procrustes + lottery-ticket sparsity | — | [#6](https://github.com/SaltyPatron/Laplace/issues/6) |
| 7 | Synthesis pipeline (LlamaTemplate + extractors + GGUFWriter) | — | [#7](https://github.com/SaltyPatron/Laplace/issues/7) |
| 8 | Round-trip + chat verification (MILESTONE) | — | [#8](https://github.com/SaltyPatron/Laplace/issues/8) |

Canonical plan in [.agent/status/plan.md](plan.md).

## In progress

None. Foundation documentation just completed; awaiting user's plan-building turn.

## Recently completed

- 2026-05-21: Created project framework — CLAUDE.md, AGENTS.md, copilot-instructions.md, README.md, GLOSSARY.md, RULES.md, STANDARDS.md, DESIGN.md, OPERATIONS.md, Justfile
- 2026-05-21: Created `.claude/agents/` — substrate-architect, postgres-extension, cpp-performance, type-taxonomy, ingestion-pipeline, verification, conventional-ai-skeptic
- 2026-05-21: Created `.claude/settings.json`
- 2026-05-21: Created `.agent/` directory with README + status skeleton

## Open blockers

See [blockers.md](blockers.md).

## Decisions log

See [decisions.md](decisions.md).

## Verifications

No verifications yet — no code to verify.
